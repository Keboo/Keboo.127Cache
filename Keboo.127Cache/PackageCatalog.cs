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
    private readonly object _refreshGate = new();
    private readonly CancellationToken _stopping;
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
    private string? _contentError;
    private bool _catalogUnreadable;
    private bool _runsUnreadable;
    private Task<Discovery>? _refreshTask;
    private PartialDiscovery? _partialDiscovery;

    private sealed record Discovery(
        RunArtifacts[] Runs, bool IsStale, string? Error, OfflineSnapshot? Snapshot,
        bool MembershipUnknown = false, int SkippedBuilds = 0, bool MetadataIncomplete = false)
    {
        public bool IsPartial => SkippedBuilds > 0 || MetadataIncomplete;
        public string Membership => MembershipUnknown ? "unknown" : IsStale ? "stale" : "current";
    }

    private sealed record OfflineResolution(
        PublishedPackage[] Packages, BuildEntry[] Builds, int AvailableBuilds, int UnavailableBuilds,
        int UnavailablePackages, int UnavailableBuildMetadata, bool UnknownInventory, HashSet<string> MissingPackageIds,
        bool DiscoveryIncomplete)
    {
        public bool IsPartial => DiscoveryIncomplete || UnavailableBuilds > 0 || UnavailablePackages > 0 || UnavailableBuildMetadata > 0;
    }

    private sealed record OnlineBuilds(BuildEntry[] Builds, Exception? Failure);
    private sealed record PartialDiscovery(OfflineSnapshot Configuration, Discovery Discovery);

    internal sealed record FeedAvailability(bool IsDegraded, bool IsPartial, string Membership);

    private sealed class FeedRequestScope(PackageCatalog catalog, FeedRequestScope? previous) : IDisposable
    {
        public Discovery? Discovery { get; set; }
        public OfflineResolution? Offline { get; set; }
        public FeedAvailability Availability { get; set; } = new(false, false, "current");
        public void Dispose() => catalog._requestScope.Value = previous;
    }

    public PackageCatalog(ProxyOptions options, IGitHubClient github, IMemoryCache memoryCache, ArtifactCache artifacts, PackageCache packages,
        ILogger<PackageCatalog>? logger = null, IHostApplicationLifetime? lifetime = null)
    {
        _options = options;
        _github = github;
        _logger = logger ?? NullLogger<PackageCatalog>.Instance;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
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
            _snapshot?.LastSuccessfulRefreshUtc, _snapshot is null ? "unknown" : "stale",
            _snapshot?.Runs.Length ?? 0, null, null, null, null);
    }

    public async Task<PublishedPackage[]> GetPackagesAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsStale)
            return (await RequireOfflinePackagesAsync(discovery, cancellationToken)).Packages;

        try
        {
            var result = new List<PublishedPackage>();
            var online = await GetOnlineBuildsAsync(discovery.Runs, cancellationToken);
            var failure = online.Failure;
            foreach (var build in online.Builds)
            {
                try
                {
                    foreach (var package in await _packages.GetPackagesAsync(build, cancellationToken))
                        result.Add(new PublishedPackage(package.Id, build with
                        {
                            OriginalVersion = package.OriginalVersion,
                            Version = package.Version
                        }));
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken)
                    && !_options.RunIds.Contains(build.RunId))
                {
                    failure ??= exception;
                }
            }
            if (failure is not null)
                return (await RecoverFromContentFailureAsync(failure, discovery, cancellationToken)).Packages;
            MarkContentHealthy(discovery);
            return result.OrderBy(x => NuGetVersion.Parse(x.Build.Version), VersionComparer.VersionRelease).ToArray();
        }
        catch (Exception exception) when (exception is not DiscoveryUnavailableException
            && DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return (await RecoverFromContentFailureAsync(exception, discovery, cancellationToken)).Packages;
        }
    }

    public async Task<BuildEntry[]> GetBuildsAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsStale)
            return (await RequireOfflinePackagesAsync(discovery, cancellationToken)).Builds;
        try
        {
            var online = await GetOnlineBuildsAsync(discovery.Runs, cancellationToken);
            return online.Failure is null ? online.Builds
                : (await RecoverFromContentFailureAsync(online.Failure, discovery, cancellationToken)).Builds;
        }
        catch (Exception exception) when (exception is not DiscoveryUnavailableException
            && DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return (await RecoverFromContentFailureAsync(exception, discovery, cancellationToken)).Builds;
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
        var partial = discovery.IsPartial || HasContentFailure();
        if (discovery.IsStale)
            partial |= (await RequireOfflinePackagesAsync(discovery, cancellationToken)).IsPartial;
        var availability = new FeedAvailability(discovery.IsStale || partial, partial, discovery.Membership);
        if (_requestScope.Value is { } scope) scope.Availability = availability;
        return availability;
    }

    internal Func<FeedAvailability> CaptureRequestAvailability()
    {
        var scope = _requestScope.Value;
        return () => scope?.Availability ?? new FeedAvailability(false, false, "current");
    }

    internal void EnsurePackageIndexComplete(string id, bool hasVersions)
    {
        var scope = _requestScope.Value;
        if (scope?.Discovery?.MetadataIncomplete == true)
            throw new DiscoveryUnavailableException("Cached build metadata is incomplete, so package versions cannot be enumerated.");
        if (scope?.Offline is { } offline
            && (offline.UnknownInventory || offline.MissingPackageIds.Contains(id)))
            throw new DiscoveryUnavailableException("A cached build may have an unavailable version of this package.");
        if (!hasVersions && scope?.Availability.IsPartial == true)
            throw new DiscoveryUnavailableException("The incomplete feed cannot determine whether this package is available.");
    }

    internal void EnsureLookupComplete()
    {
        if (_requestScope.Value?.Availability.IsPartial == true)
            throw new DiscoveryUnavailableException("The incomplete feed cannot determine whether this package version is available.");
    }

    internal void EnsureAggregateComplete()
    {
        if (_requestScope.Value?.Availability.IsPartial == true)
            throw new DiscoveryUnavailableException("The cached feed is incomplete and cannot enumerate a complete result.");
    }

    internal string ReportWarmupFailure(Exception exception)
    {
        var current = GetHealth();
        if (exception is DiscoveryUnavailableException && current.Status == "degraded" && current.Error is not null)
            return current.Error;
        var error = DiscoveryFailure.Describe(exception);
        SetFailureHealth(error, FindFallback(error));
        return error;
    }

    internal string ReportLateContentFailure(Exception exception)
    {
        _memoryCache.Remove(_cacheKey);
        var error = DiscoveryFailure.DescribeContent(exception);
        SetFailureHealth(error, FindFallback(error), contentFailure: true);
        _logger.LogWarning("Build content is unavailable: {Error}", error);
        return error;
    }

    private async Task<OnlineBuilds> GetOnlineBuildsAsync(RunArtifacts[] runs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = new List<BuildEntry>(_builds);
            Exception? failure = null;
            var changed = false;
            foreach (var run in runs)
            {
                if (next.Any(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest
                    && x.Packages.Id == run.Packages.Id && x.VersionArtifact?.Id == run.VersionArtifact.Id)) continue;
                try
                {
                    var original = await ReadVersionAsync(run.VersionArtifact, cancellationToken);
                    next.RemoveAll(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest);
                    next.Add(new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, original,
                        PackageRewriter.VersionFor(original, run.PullRequest, run.RunId, run.Attempt, run.Packages.Id),
                        run.Packages, run.VersionArtifact));
                    changed = true;
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken)
                    && !_options.RunIds.Contains(run.RunId))
                {
                    failure ??= exception;
                }
            }
            if (changed)
            {
                await JsonSettings.WriteAtomicAsync(_file, new CatalogFile(_options.Repository, next), cancellationToken);
                _builds = next;
            }
            var visible = runs.Select(x => (x.RunId, x.Attempt, x.PullRequest, x.Packages.Id, x.VersionArtifact.Id)).ToHashSet();
            return new OnlineBuilds(_builds.Where(x => x.VersionArtifact is not null
                    && visible.Contains((x.RunId, x.Attempt, x.PullRequest, x.Packages.Id, x.VersionArtifact.Id)))
                .OrderBy(x => NuGetVersion.Parse(x.Version), VersionComparer.VersionRelease).ToArray(), failure);
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
            return Remember(scope, new Discovery(cached, false, null, MatchingSnapshot()));

        var refresh = StartRefresh();
        if (!refresh.IsCompleted)
        {
            var fallback = FindFallback(GetHealth().Error ?? "GitHub discovery is in progress.");
            if (fallback is not null && fallback.Runs.Any(_packages.MightHaveCachedContent))
                return Remember(scope, fallback);
        }
        return Remember(scope, await refresh.WaitAsync(cancellationToken));
    }

    private Task<Discovery> StartRefresh()
    {
        lock (_refreshGate)
        {
            if (_refreshTask is { IsCompleted: false }) return _refreshTask;
            if (_memoryCache.TryGetValue(_cacheKey, out RunArtifacts[]? cached) && cached is not null)
                return Task.FromResult(new Discovery(cached, false, null, MatchingSnapshot()));
            var pending = FindFallback("GitHub discovery is in progress.");
            var health = GetHealth();
            if (pending is not null && (health.Status == "ok" || health.Error == "Initial GitHub discovery has not completed."))
                SetFailureHealth(pending.Error!, pending);
            return _refreshTask = RefreshAndHandleFailureAsync(_stopping);
        }
    }

    private async Task<Discovery> RefreshAndHandleFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            var error = DiscoveryFailure.Describe(exception);
            var snapshot = MatchingSnapshot();
            var fallback = FindFallback(error);
            if (fallback is null && snapshot is null)
                error += " " + (_snapshotError ?? "No matching successful discovery snapshot is available.");
            else if (fallback is null && snapshot?.Runs.Length == 0)
                error += " The last successful snapshot contained no builds, so no offline feed is available.";
            if (fallback is not null) error = fallback.Error!;
            SetFailureHealth(error, fallback);
            _logger.LogWarning("Catalog discovery failed: {Error}", error);
            return fallback ?? throw new DiscoveryUnavailableException(error);
        }
    }

    private async Task<Discovery> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_catalogUnreadable || _runsUnreadable)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_catalogUnreadable) LoadCatalog();
                if (_runsUnreadable) LoadRuns();
                if (_catalogUnreadable || _runsUnreadable)
                    throw new CacheMetadataUnavailableException();
            }
            finally
            {
                _gate.Release();
            }
        }
        var openPullRequests = (await _github.GetOpenPullRequestsAsync(cancellationToken)).Select(x => x.Number).ToHashSet();
        var discovered = openPullRequests.Count == 0 ? [] : await _github.GetRunsAsync(cancellationToken);
        var eligibleRuns = discovered
            .Where(x => x.Status == "completed" && x.Conclusion == "success" && x.Event == "pull_request")
            .SelectMany(run => run.PullRequests.Where(pr => openPullRequests.Contains(pr.Number))
                .Where(pr => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(pr.Number) || _options.RunIds.Contains(run.Id))
                .Select(pr => (Run: run, PullRequest: pr.Number)))
            .ToArray();
        // A live workflow listing can corroborate cached runs absent from historical run metadata.
        var cachedBuilds = _builds.Select(build => (build.RunId, build.Attempt, build.PullRequest)).ToHashSet();
        var runs = eligibleRuns.GroupBy(x => x.PullRequest)
            .SelectMany(g => g.OrderByDescending(x => x.Run.Id).Take(1).Concat(g.Where(x =>
                _options.RunIds.Contains(x.Run.Id)
                || cachedBuilds.Contains((x.Run.Id, x.Run.Attempt, x.PullRequest)))))
            .DistinctBy(x => (x.Run.Id, x.Run.Attempt, x.PullRequest)).ToArray();
        // Keep immutable completed-run and artifact metadata on disk even while a PR is closed.
        // Open-PR membership is volatile and is applied only to the response below.
        var next = new List<RunArtifacts>(_runs);
        if (MatchingSnapshot() is { } knownSnapshot)
            foreach (var run in knownSnapshot.Runs)
                if (!next.Any(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest))
                    next.Add(run);
        var skipped = 0;
        string? skippedError = null;
        foreach (var (run, pr) in runs)
        {
            // Completed immutable builds already known locally need no artifact API/download request.
            if (next.Any(x => x.RunId == run.Id && x.Attempt == run.Attempt && x.PullRequest == pr
                && x.Packages.Name == _options.PackagesArtifact && x.VersionArtifact.Name == _options.VersionArtifact)) continue;
            var artifacts = await _github.GetArtifactsAsync(run.Id, cancellationToken);
            var packages = artifacts.SingleOrDefault(x => x.Name == _options.PackagesArtifact);
            var version = artifacts.SingleOrDefault(x => x.Name == _options.VersionArtifact);
            var problem = packages is null || version is null
                ? $"Run {run.Id} has missing package/version artifacts."
                : (packages.Expired && !_artifacts.Contains(packages)) || (version.Expired && !_artifacts.Contains(version))
                    ? $"Run {run.Id} has expired artifacts that are not cached locally."
                    : null;
            if (problem is not null)
            {
                if (_options.RunIds.Contains(run.Id)) throw new InvalidDataException(problem);
                skipped++;
                skippedError ??= problem;
                _logger.LogWarning("Skipping an unpinned build: {Error}", problem);
                continue;
            }
            next.RemoveAll(x => x.RunId == run.Id && x.Attempt == run.Attempt && x.PullRequest == pr);
            next.Add(new RunArtifacts(run.Id, run.Attempt, pr, run.HeadSha, packages!, version!));
        }
        foreach (var id in _options.RunIds)
        {
            if (discovered.Any(x => x.Id == id && x.PullRequests.Any(pr => openPullRequests.Contains(pr.Number)))
                && !next.Any(x => x.RunId == id))
                throw new InvalidDataException($"Run {id} is not an available successful build of an open pull request.");
        }
        var result = next.Where(x => openPullRequests.Contains(x.PullRequest))
            .Where(x => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(x.PullRequest)
                || _options.RunIds.Contains(x.RunId))
            .Where(x => x.Packages.Name == _options.PackagesArtifact && x.VersionArtifact.Name == _options.VersionArtifact)
            .OrderBy(x => x.RunId).ThenBy(x => x.Attempt).ToArray();
        var metadataIncomplete = HasUnmatchedCatalogBuilds(result, openPullRequests);
        try
        {
            await JsonSettings.WriteAtomicAsync(_runsFile,
                new RunArtifactsFile(_options.Repository, _options.Workflow, next), cancellationToken);
            _runs = next;
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            throw new SnapshotPersistenceException();
        }
        if (skipped > 0 || metadataIncomplete)
        {
            if (result.Length == 0)
            {
                if (skipped > 0) throw new InvalidDataException(skippedError);
                throw new CacheMetadataUnavailableException();
            }
            var error = skipped > 0 ? skippedError + " Discovery is incomplete; unpinned unavailable builds were skipped." : "";
            if (metadataIncomplete) error += " Cached catalog and run metadata do not agree; package inventories may be incomplete.";
            var incomplete = new Discovery(result, false, error.Trim(), MatchingSnapshot(),
                SkippedBuilds: skipped, MetadataIncomplete: metadataIncomplete);
            Volatile.Write(ref _partialDiscovery, new PartialDiscovery(OfflineSnapshot.Create(_options, []), incomplete));
            SetFailureHealth(incomplete.Error!, incomplete);
            return incomplete;
        }

        var snapshot = OfflineSnapshot.Create(_options, result);
        try
        {
            await JsonSettings.WriteAtomicAsync(_snapshotFile, snapshot, cancellationToken);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            throw new SnapshotPersistenceException();
        }
        _snapshot = snapshot;
        _snapshotError = null;
        Volatile.Write(ref _partialDiscovery, null);
        lock (_healthGate)
            _health = new CatalogHealth(_contentError is null ? "ok" : "degraded", _options.Repository, _contentError,
                snapshot.LastSuccessfulRefreshUtc,
                "current", snapshot.Runs.Length, null, null, null, null);
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

    private Discovery? FindFallback(string error)
    {
        var partial = Volatile.Read(ref _partialDiscovery);
        if (partial is not null && partial.Configuration.Matches(_options))
            return partial.Discovery with { IsStale = true, Error = error };
        var snapshot = MatchingSnapshot();
        if (snapshot is not null)
            return snapshot.Runs.Length == 0 ? null : new Discovery(snapshot.Runs, true, error, snapshot);
        if (File.Exists(_snapshotFile) || !File.Exists(_runsFile) || _runsUnreadable) return null;
        var runs = _runs.Where(run => run.Packages.Name == _options.PackagesArtifact
                && run.VersionArtifact.Name == _options.VersionArtifact
                && (_options.PullRequests.Length == 0 || _options.PullRequests.Contains(run.PullRequest)
                    || _options.RunIds.Contains(run.RunId)))
            .OrderBy(run => run.RunId).ThenBy(run => run.Attempt).ToArray();
        if (runs.Length == 0) return null;
        const string legacy = "Legacy cache open-PR membership is unknown; closed PRs may be included.";
        if (!error.Contains(legacy, StringComparison.Ordinal)) error += " " + legacy;
        var metadataIncomplete = HasUnmatchedCatalogBuilds(runs, null);
        const string metadata = "Legacy catalog and run metadata do not agree; package inventories may be incomplete.";
        if (metadataIncomplete && !error.Contains(metadata, StringComparison.Ordinal)) error += " " + metadata;
        return new Discovery(runs, true, error,
            null, MembershipUnknown: true, MetadataIncomplete: metadataIncomplete);
    }

    private bool HasUnmatchedCatalogBuilds(RunArtifacts[] runs, IReadOnlySet<int>? openPullRequests) =>
        _catalogUnreadable || _builds.Any(build =>
            (openPullRequests is null || openPullRequests.Contains(build.PullRequest))
            && (_options.PullRequests.Length == 0 || _options.PullRequests.Contains(build.PullRequest)
                || _options.RunIds.Contains(build.RunId))
            && build.Packages.Name == _options.PackagesArtifact
            && (build.VersionArtifact is null || build.VersionArtifact.Name == _options.VersionArtifact)
            && !runs.Any(run => run.RunId == build.RunId && run.Attempt == build.Attempt
                && run.PullRequest == build.PullRequest && run.Packages.Id == build.Packages.Id
                && run.VersionArtifact.Id == build.VersionArtifact?.Id));

    private bool HasContentFailure()
    {
        lock (_healthGate) return _contentError is not null;
    }

    private void MarkContentHealthy(Discovery discovery)
    {
        if (discovery.IsPartial || discovery.Snapshot is null) return;
        lock (_healthGate)
        {
            if (_contentError is null || _health.Membership != "current"
                || _health.LastSuccessfulRefreshUtc != discovery.Snapshot.LastSuccessfulRefreshUtc) return;
            _contentError = null;
            _health = _health with
            {
                Status = "ok",
                Error = null,
                AvailableCachedBuilds = null,
                UnavailableBuilds = null,
                UnavailablePackages = null,
                UnavailableBuildMetadata = null
            };
        }
        if (_requestScope.Value is { } scope)
            scope.Availability = new FeedAvailability(false, false, "current");
    }

    private void SetFailureHealth(string error, Discovery? discovery, bool contentFailure = false)
    {
        var snapshot = discovery?.Snapshot ?? MatchingSnapshot();
        lock (_healthGate)
        {
            if (contentFailure) _contentError = error;
            _health = new CatalogHealth("degraded", _options.Repository, error, snapshot?.LastSuccessfulRefreshUtc,
                discovery?.Membership ?? (snapshot is null ? "unknown" : "stale"),
                discovery?.Runs.Length ?? snapshot?.Runs.Length ?? 0, null,
                discovery is { SkippedBuilds: > 0 } ? discovery.SkippedBuilds : null, null, null);
        }
    }

    private async Task<OfflineResolution> RequireOfflinePackagesAsync(Discovery discovery, CancellationToken cancellationToken)
    {
        var resolution = await ResolveOfflineAsync(discovery, cancellationToken);
        if (resolution.Packages.Length == 0)
            throw new DiscoveryUnavailableException(discovery.Error + " No usable cached packages are available for this snapshot.");
        return resolution;
    }

    private async Task<OfflineResolution> RecoverFromContentFailureAsync(
        Exception exception, Discovery source, CancellationToken cancellationToken)
    {
        _memoryCache.Remove(_cacheKey);
        var error = DiscoveryFailure.DescribeContent(exception);
        var discovery = source with { IsStale = true, Error = error };
        SetFailureHealth(error, discovery, contentFailure: true);
        _logger.LogWarning("Build content is unavailable: {Error}", error);
        if (discovery.Runs.Length == 0)
            throw new DiscoveryUnavailableException(error + " No matching successful discovery snapshot is available.");
        var scope = _requestScope.Value;
        if (scope is not null)
        {
            scope.Discovery = discovery;
            scope.Offline = null;
        }
        var resolution = await RequireOfflinePackagesAsync(discovery, cancellationToken);
        if (scope is not null) scope.Availability = new FeedAvailability(true, resolution.IsPartial, discovery.Membership);
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
            var unavailableBuilds = discovery.SkippedBuilds;
            var unavailablePackages = 0;
            var unavailableMetadata = 0;
            var unknownInventory = discovery.MetadataIncomplete;
            var missingPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    available = new CachedPackageAvailability([], [], false);
                }
                unavailablePackages += available.MissingPackages;
                missingPackageIds.UnionWith(available.MissingPackageIds);
                unknownInventory |= !available.HasInventory;
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
                availableBuilds, unavailableBuilds, unavailablePackages, unavailableMetadata,
                unknownInventory, missingPackageIds, discovery.IsPartial || discovery.MembershipUnknown);
            lock (_healthGate)
            {
                if (result.UnavailableBuilds > discovery.SkippedBuilds || result.UnavailablePackages > 0
                    || result.UnavailableBuildMetadata > 0 || discovery.MetadataIncomplete)
                    _contentError ??= "Cached build content or metadata is incomplete or invalid.";
                if (_health.Status == "degraded"
                    && _health.LastSuccessfulRefreshUtc == discovery.Snapshot?.LastSuccessfulRefreshUtc
                    && _health.Membership == discovery.Membership
                    && _health.SnapshotBuilds == discovery.Runs.Length)
                {
                    var detail = result.Packages.Length == 0
                        ? " No usable cached packages are available for this snapshot."
                        : result.IsPartial ? " Some snapshot content is not cached or is incomplete." : "";
                    var error = _health.Error ?? discovery.Error ?? "";
                    if (detail.Length > 0 && !error.Contains(detail, StringComparison.Ordinal)) error += detail;
                    _health = _health with
                    {
                        Error = error,
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
            // catalog.json identifies the repository, but not the workflow.
            _runs = [];
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