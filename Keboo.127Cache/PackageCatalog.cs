using System.IO.Compression;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;

using NuGet.Versioning;

namespace Keboo._127Cache;

public sealed record BuildEntry(
    long RunId, int Attempt, int PullRequest, string HeadSha, string OriginalVersion, string Version,
    Artifact Packages, Artifact VersionArtifact);
public sealed record CatalogFile(string Repository, List<BuildEntry> Builds);
public sealed record PublishedPackage(string Id, BuildEntry Build);
public sealed record RunArtifacts(
    long RunId, int Attempt, int PullRequest, string HeadSha, Artifact Packages, Artifact VersionArtifact);
public sealed record RunArtifactsFile(string Repository, string Workflow, List<RunArtifacts> Runs);

public sealed class PackageCatalog
{
    private readonly ProxyOptions _options;
    private readonly IGitHubClient _github;
    private readonly ILogger<PackageCatalog> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly ArtifactCache _artifacts;
    private readonly PackageCache _packages;
    private readonly object _cacheKey = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _file;
    private readonly string _runsFile;
    private readonly string _snapshotFile;
    private readonly object _healthGate = new();
    private readonly AsyncLocal<FeedRequestScope?> _requestScope = new();
    private List<RunArtifacts> _runs;
    private List<BuildEntry> _builds;
    private OfflineSnapshot? _snapshot;
    private string? _snapshotError;
    private CatalogHealth _health;
    private bool _catalogUnreadable;
    private bool _runsUnreadable;

    private sealed record Discovery(RunArtifacts[] Runs, bool IsStale, string? Error, OfflineSnapshot? Snapshot);
    private sealed record OfflineResolution(
        PublishedPackage[] Packages, BuildEntry[] Builds, int AvailableBuilds, int UnavailableBuilds,
        int UnavailablePackages, int UnavailableBuildMetadata)
    {
        public bool IsPartial => UnavailableBuilds > 0 || UnavailablePackages > 0 || UnavailableBuildMetadata > 0;
    }

    internal sealed record FeedAvailability(bool IsDegraded, bool IsPartial);

    private sealed class FeedRequestScope(PackageCatalog catalog, FeedRequestScope? previous) : IDisposable
    {
        public Discovery? Discovery { get; set; }
        public OfflineResolution? Offline { get; set; }
        public FeedAvailability Availability { get; set; } = new(false, false);
        public void Dispose() => catalog._requestScope.Value = previous;
    }

    public PackageCatalog(ProxyOptions options, IGitHubClient github, IMemoryCache memoryCache, ArtifactCache artifacts, PackageCache packages,
        ILogger<PackageCatalog>? logger = null)
    {
        _options = options;
        _github = github;
        _logger = logger ?? NullLogger<PackageCatalog>.Instance;
        _memoryCache = memoryCache;
        _artifacts = artifacts;
        _packages = packages;
        _file = Path.Combine(options.CacheDirectory, "catalog.json");
        Directory.CreateDirectory(options.CacheDirectory);
        _builds = [];
        _runs = [];
        _runsFile = Path.Combine(options.CacheDirectory, "runs.json");
        LoadCatalog();
        LoadRuns();
        _snapshotFile = Path.Combine(options.CacheDirectory, "visible-runs.json");
        _snapshot = OfflineSnapshot.Load(_snapshotFile, options, out _snapshotError);
        _health = new CatalogHealth("degraded", options.Repository, "Initial GitHub discovery has not completed.",
            _snapshot?.LastSuccessfulRefreshUtc, _snapshot?.Runs.Length ?? 0, null, null, null, null);
    }

    public async Task<PublishedPackage[]> GetPackagesAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsStale)
            return (await RequireOfflinePackagesAsync(discovery, cancellationToken)).Packages;

        try
        {
            var result = new List<PublishedPackage>();
            foreach (var build in await GetOnlineBuildsAsync(discovery.Runs, cancellationToken))
            {
                foreach (var package in await _packages.GetPackagesAsync(build, cancellationToken))
                    result.Add(new PublishedPackage(package.Id, build with
                    {
                        OriginalVersion = package.OriginalVersion,
                        Version = package.Version
                    }));
            }
            return result.OrderBy(x => NuGetVersion.Parse(x.Build.Version), VersionComparer.VersionRelease).ToArray();
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return (await RecoverFromContentFailureAsync(exception, cancellationToken)).Packages;
        }
    }

    public async Task<BuildEntry[]> GetBuildsAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsStale)
            return (await RequireOfflinePackagesAsync(discovery, cancellationToken)).Builds;
        try
        {
            return await GetOnlineBuildsAsync(discovery.Runs, cancellationToken);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return (await RecoverFromContentFailureAsync(exception, cancellationToken)).Builds;
        }
    }

    public async Task<RunArtifacts[]> GetRunArtifactsAsync(CancellationToken cancellationToken) =>
        (await DiscoverAsync(cancellationToken)).Runs;

    public CatalogHealth GetHealth()
    {
        lock (_healthGate) return _health;
    }

    internal IDisposable BeginFeedRequest()
    {
        var scope = new FeedRequestScope(this, _requestScope.Value);
        _requestScope.Value = scope;
        return scope;
    }

    internal async Task<FeedAvailability> EnsureFeedAvailableAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);
        if (!discovery.IsStale) return new(false, false);
        var offline = await RequireOfflinePackagesAsync(discovery, cancellationToken);
        var availability = new FeedAvailability(true, offline.IsPartial);
        if (_requestScope.Value is { } scope) scope.Availability = availability;
        return availability;
    }

    internal Func<FeedAvailability> CaptureRequestAvailability()
    {
        var scope = _requestScope.Value;
        return () => scope?.Availability ?? new FeedAvailability(false, false);
    }

    internal string ReportWarmupFailure(Exception exception)
    {
        var current = GetHealth();
        if (exception is DiscoveryUnavailableException && current.Status == "degraded" && current.Error is not null)
            return current.Error;
        var error = DiscoveryFailure.Describe(exception);
        SetFailureHealth(error, MatchingSnapshot());
        return error;
    }

    internal string ReportLateContentFailure(Exception exception)
    {
        _memoryCache.Remove(_cacheKey);
        var error = DiscoveryFailure.DescribeContent(exception);
        SetFailureHealth(error, MatchingSnapshot());
        _logger.LogWarning("Build content is unavailable: {Error}", error);
        return error;
    }

    private async Task<BuildEntry[]> GetOnlineBuildsAsync(RunArtifacts[] runs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = new List<BuildEntry>(_builds);
            foreach (var run in runs)
            {
                if (next.Any(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest)) continue;
                var original = await ReadVersionAsync(run.VersionArtifact, cancellationToken);
                next.Add(new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, original,
                    PackageRewriter.VersionFor(original, run.PullRequest, run.RunId, run.Attempt, run.Packages.Id),
                    run.Packages, run.VersionArtifact));
            }
            if (next.Count != _builds.Count)
            {
                await JsonSettings.WriteAtomicAsync(_file, new CatalogFile(_options.Repository, next), cancellationToken);
                _builds = next;
            }
            var visible = runs.Select(x => (x.RunId, x.Attempt, x.PullRequest)).ToHashSet();
            return _builds.Where(x => visible.Contains((x.RunId, x.Attempt, x.PullRequest)))
                .OrderBy(x => NuGetVersion.Parse(x.Version), VersionComparer.VersionRelease).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Discovery> DiscoverAsync(CancellationToken cancellationToken)
    {
        var scope = _requestScope.Value;
        if (scope?.Discovery is { } requestDiscovery) return requestDiscovery;
        if (_memoryCache.TryGetValue(_cacheKey, out RunArtifacts[]? cached) && cached is not null)
            return Remember(scope, new Discovery(cached, false, null, null));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache.TryGetValue(_cacheKey, out cached) && cached is not null)
                return Remember(scope, new Discovery(cached, false, null, null));

            try
            {
                var result = await RefreshAsync(cancellationToken);
                return Remember(scope, result);
            }
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
            {
                var error = DiscoveryFailure.Describe(exception);
                var snapshot = MatchingSnapshot();
                if (snapshot is null)
                    error += " " + (_snapshotError ?? "No matching successful discovery snapshot is available.");
                else if (snapshot.Runs.Length == 0)
                    error += " The last successful snapshot contained no builds, so no offline feed is available.";
                SetFailureHealth(error, snapshot);
                _logger.LogWarning("Catalog discovery failed: {Error}", error);
                if (snapshot is null || snapshot.Runs.Length == 0)
                    throw new DiscoveryUnavailableException(error);
                return Remember(scope, new Discovery(snapshot.Runs, true, error, snapshot));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Discovery> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_catalogUnreadable) LoadCatalog();
        if (_runsUnreadable) LoadRuns();
        if (_catalogUnreadable || _runsUnreadable)
            throw new CacheMetadataUnavailableException();
        var openPullRequests = (await _github.GetOpenPullRequestsAsync(cancellationToken)).Select(x => x.Number).ToHashSet();
        var discovered = openPullRequests.Count == 0 ? [] : await _github.GetRunsAsync(cancellationToken);
        var eligibleRuns = discovered
            .Where(x => x.Status == "completed" && x.Conclusion == "success" && x.Event == "pull_request")
            .SelectMany(run => run.PullRequests.Where(pr => openPullRequests.Contains(pr.Number))
                .Where(pr => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(pr.Number) || _options.RunIds.Contains(run.Id))
                .Select(pr => (Run: run, PullRequest: pr.Number)))
            .ToArray();
        var runs = eligibleRuns.GroupBy(x => x.PullRequest)
            .SelectMany(g => g.OrderByDescending(x => x.Run.Id).Take(1).Concat(g.Where(x => _options.RunIds.Contains(x.Run.Id))))
            .DistinctBy(x => (x.Run.Id, x.Run.Attempt, x.PullRequest)).ToArray();
        // Keep immutable completed-run and artifact metadata on disk even while a PR is closed.
        // Open-PR membership is volatile and is applied only to the response below.
        var next = new List<RunArtifacts>(_runs);
        foreach (var (run, pr) in runs)
        {
            // Completed immutable builds already known locally need no artifact API/download request.
            if (next.Any(x => x.RunId == run.Id && x.Attempt == run.Attempt && x.PullRequest == pr)) continue;
            var artifacts = await _github.GetArtifactsAsync(run.Id, cancellationToken);
            var packages = artifacts.SingleOrDefault(x => x.Name == _options.PackagesArtifact);
            var version = artifacts.SingleOrDefault(x => x.Name == _options.VersionArtifact);
            if (packages is null || version is null)
                throw new InvalidDataException($"Run {run.Id} has missing package/version artifacts.");
            if ((packages.Expired && !_artifacts.Contains(packages)) || (version.Expired && !_artifacts.Contains(version)))
                throw new InvalidDataException($"Run {run.Id} has expired artifacts that are not cached locally.");
            next.Add(new RunArtifacts(run.Id, run.Attempt, pr, run.HeadSha, packages, version));
        }
        foreach (var id in _options.RunIds)
        {
            if (discovered.Any(x => x.Id == id && x.PullRequests.Any(pr => openPullRequests.Contains(pr.Number)))
                && !next.Any(x => x.RunId == id))
                throw new InvalidDataException($"Run {id} is not an available successful build of an open pull request.");
        }
        var visible = runs.Select(x => (x.Run.Id, x.Run.Attempt, x.PullRequest)).ToHashSet();
        var result = next.Where(x => visible.Contains((x.RunId, x.Attempt, x.PullRequest)))
            .OrderBy(x => x.RunId).ThenBy(x => x.Attempt).ToArray();
        var snapshot = OfflineSnapshot.Create(_options, result);
        try
        {
            await JsonSettings.WriteAtomicAsync(_runsFile,
                new RunArtifactsFile(_options.Repository, _options.Workflow, next), cancellationToken);
            _runs = next;
            await JsonSettings.WriteAtomicAsync(_snapshotFile, snapshot, cancellationToken);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            throw new SnapshotPersistenceException();
        }
        _snapshot = snapshot;
        _snapshotError = null;
        lock (_healthGate)
            _health = new CatalogHealth("ok", _options.Repository, null, snapshot.LastSuccessfulRefreshUtc,
                snapshot.Runs.Length, null, null, null, null);
        _memoryCache.Set(_cacheKey, result, TimeSpan.FromSeconds(_options.RefreshSeconds));
        return new Discovery(result, false, null, snapshot);
    }

    private static Discovery Remember(FeedRequestScope? scope, Discovery discovery)
    {
        if (scope is not null) scope.Discovery = discovery;
        return discovery;
    }

    private OfflineSnapshot? MatchingSnapshot() =>
        _snapshot is { } snapshot && snapshot.Matches(_options) ? snapshot : null;

    private void SetFailureHealth(string error, OfflineSnapshot? snapshot)
    {
        lock (_healthGate)
            _health = new CatalogHealth("degraded", _options.Repository, error, snapshot?.LastSuccessfulRefreshUtc,
                snapshot?.Runs.Length ?? 0, null, null, null, null);
    }

    private async Task<OfflineResolution> RequireOfflinePackagesAsync(Discovery discovery, CancellationToken cancellationToken)
    {
        var resolution = await ResolveOfflineAsync(discovery, cancellationToken);
        if (resolution.Packages.Length == 0)
            throw new DiscoveryUnavailableException(discovery.Error + " No usable cached packages are available for this snapshot.");
        return resolution;
    }

    private async Task<OfflineResolution> RecoverFromContentFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        _memoryCache.Remove(_cacheKey);
        var error = DiscoveryFailure.DescribeContent(exception);
        var snapshot = MatchingSnapshot();
        SetFailureHealth(error, snapshot);
        _logger.LogWarning("Build content is unavailable: {Error}", error);
        if (snapshot is null || snapshot.Runs.Length == 0)
            throw new DiscoveryUnavailableException(error + " No matching successful discovery snapshot is available.");
        var discovery = new Discovery(snapshot.Runs, true, error, snapshot);
        var scope = _requestScope.Value;
        if (scope is not null)
        {
            scope.Discovery = discovery;
            scope.Offline = null;
        }
        var resolution = await RequireOfflinePackagesAsync(discovery, cancellationToken);
        if (scope is not null) scope.Availability = new FeedAvailability(true, resolution.IsPartial);
        return resolution;
    }

    private async Task<OfflineResolution> ResolveOfflineAsync(Discovery discovery, CancellationToken cancellationToken)
    {
        var scope = _requestScope.Value;
        if (scope?.Offline is { } cached) return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packages = new List<PublishedPackage>();
            var builds = new List<BuildEntry>();
            var availableBuilds = 0;
            var unavailableBuilds = 0;
            var unavailablePackages = 0;
            var unavailableMetadata = 0;
            foreach (var run in discovery.Runs)
            {
                var build = _builds.FirstOrDefault(x => x.RunId == run.RunId && x.Attempt == run.Attempt
                    && x.PullRequest == run.PullRequest && x.Packages is not null && x.Packages.Id == run.Packages.Id
                    && x.VersionArtifact is not null && x.VersionArtifact.Id == run.VersionArtifact.Id);
                if (build is null && _artifacts.Contains(run.VersionArtifact))
                {
                    try
                    {
                        var original = await ReadVersionAsync(run.VersionArtifact, cancellationToken);
                        build = new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, original,
                            PackageRewriter.VersionFor(original, run.PullRequest, run.RunId, run.Attempt, run.Packages.Id),
                            run.Packages, run.VersionArtifact);
                    }
                    catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                    {
                    }
                }

                var template = build ?? new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, "", "",
                    run.Packages, run.VersionArtifact);
                CachedPackageAvailability available;
                try
                {
                    available = await _packages.GetAvailableOfflineAsync(template, cancellationToken);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                {
                    available = new CachedPackageAvailability([], 0, false);
                }
                unavailablePackages += available.MissingPackages;
                if (!available.HasInventory || available.Packages.Length == 0)
                {
                    unavailableBuilds++;
                    continue;
                }
                availableBuilds++;
                if (build is null) unavailableMetadata++;
                else builds.Add(build);
                foreach (var package in available.Packages)
                    packages.Add(new PublishedPackage(package.Id, template with
                    {
                        OriginalVersion = package.OriginalVersion,
                        Version = package.Version
                    }));
            }

            var result = new OfflineResolution(
                packages.OrderBy(x => NuGetVersion.Parse(x.Build.Version), VersionComparer.VersionRelease).ToArray(),
                builds.OrderBy(x => NuGetVersion.Parse(x.Version), VersionComparer.VersionRelease).ToArray(),
                availableBuilds, unavailableBuilds, unavailablePackages, unavailableMetadata);
            lock (_healthGate)
            {
                if (_health.Status == "degraded"
                    && _health.LastSuccessfulRefreshUtc == discovery.Snapshot?.LastSuccessfulRefreshUtc)
                {
                    var detail = result.Packages.Length == 0
                        ? " No usable cached packages are available for this snapshot."
                        : result.IsPartial ? " Some snapshot content is not cached or is incomplete." : "";
                    _health = _health with
                    {
                        Error = discovery.Error + detail,
                        AvailableCachedBuilds = result.AvailableBuilds,
                        UnavailableBuilds = result.UnavailableBuilds,
                        UnavailablePackages = result.UnavailablePackages,
                        UnavailableBuildMetadata = result.UnavailableBuildMetadata
                    };
                }
            }
            if (scope is not null) scope.Offline = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadCatalog()
    {
        if (!File.Exists(_file))
        {
            _builds = [];
            _catalogUnreadable = false;
            return;
        }

        CatalogFile saved;
        try
        {
            saved = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(_file), JsonSettings.Options)
                ?? throw new JsonException("Invalid saved catalog.");
            if (saved.Builds is null || saved.Builds.Any(build => build is null || build.RunId <= 0
                || build.Attempt <= 0 || build.PullRequest <= 0 || build.HeadSha is null
                || !NuGetVersion.TryParse(build.OriginalVersion, out _) || !NuGetVersion.TryParse(build.Version, out _)
                || build.Packages is null || build.Packages.Id <= 0
                || build.VersionArtifact is { Id: <= 0 })
                || saved.Builds.Select(build => (build.RunId, build.Attempt, build.PullRequest)).Distinct().Count() != saved.Builds.Count)
                throw new JsonException("Invalid saved catalog builds.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _catalogUnreadable = true;
            return;
        }
        if (saved.Repository != _options.Repository)
            throw new InvalidDataException("This cache belongs to another repository. Use a different CacheDirectory.");
        _builds = saved.Builds;
        _catalogUnreadable = false;
    }

    private void LoadRuns()
    {
        if (!File.Exists(_runsFile))
        {
            _runs = _builds.Where(x => x.VersionArtifact is not null && x.Packages is not null)
                .Select(x => new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact))
                .ToList();
            _runsUnreadable = false;
            return;
        }

        RunArtifactsFile saved;
        try
        {
            saved = JsonSerializer.Deserialize<RunArtifactsFile>(File.ReadAllText(_runsFile), JsonSettings.Options)
                ?? throw new JsonException("Invalid saved run metadata.");
            if (saved.Runs is null || saved.Runs.Any(run => run is null || run.RunId <= 0 || run.Attempt <= 0
                || run.PullRequest <= 0 || run.HeadSha is null || run.Packages is null || run.Packages.Id <= 0
                || run.VersionArtifact is null || run.VersionArtifact.Id <= 0)
                || saved.Runs.Select(run => (run.RunId, run.Attempt, run.PullRequest)).Distinct().Count() != saved.Runs.Count)
                throw new JsonException("Invalid saved runs.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _runsUnreadable = true;
            return;
        }
        if (saved.Repository != _options.Repository || saved.Workflow != _options.Workflow)
            throw new InvalidDataException("This run cache belongs to another repository/workflow. Use a different CacheDirectory.");
        _runs = saved.Runs;
        _runsUnreadable = false;
    }

    private async Task<string> ReadVersionAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var path = await _artifacts.GetAsync(artifact, cancellationToken);
        using var archive = ZipFile.OpenRead(path);
        var matches = archive.Entries.Where(x => x.FullName == "version.txt").Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Version artifact must contain one version.txt.");
        var entry = matches[0];
        if (entry.Length > 4096) throw new InvalidDataException("Version file is unexpectedly large.");
        using var reader = new StreamReader(entry.Open());
        var value = (await reader.ReadToEndAsync(cancellationToken)).Trim();
        if (!NuGetVersion.TryParse(value, out var version)) throw new InvalidDataException("Version file is invalid.");
        return version.ToNormalizedString();
    }
}