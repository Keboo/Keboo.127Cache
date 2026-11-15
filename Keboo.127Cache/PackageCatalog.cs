using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Keboo.LocalCache;

// Aggregates one SourceCatalog per configured build source. With a single healthy source every call is delegated unchanged.
// With several sources a failing source never takes down the others, but its absence is never served as a complete feed.
public sealed class PackageCatalog : IDisposable, IAsyncDisposable
{
    private const string SourceKey = "127cache.source";

    private sealed record FailedSource(string Name, string Repository, Exception Error);

    private sealed class AggregateScope(PackageCatalog catalog, AggregateScope? previous, IDisposable[] inner) : IDisposable
    {
        public Dictionary<SourceCatalog, Exception> Failed { get; } = [];
        public bool Partial { get; set; }
        public SourceCatalog.FeedAvailability? Availability { get; set; }
        public void Dispose()
        {
            catalog._scope.Value = previous;
            foreach (var disposable in inner) disposable.Dispose();
        }
    }

    private readonly ProxyOptions _options;
    private readonly List<SourceCatalog> _engines;
    private readonly List<FailedSource> _failed;
    private readonly ILogger _logger;
    private readonly AsyncLocal<AggregateScope?> _scope = new();
    private readonly HashSet<string> _loggedCollisions = [];
    private bool Single => _engines.Count == 1 && _failed.Count == 0;

    // Wraps one GitHub Actions source that owns the cache directory of `options` directly.
    public PackageCatalog(ProxyOptions options, IGitHubClient github, IMemoryCache memoryCache, ArtifactCache artifacts,
        PackageCache packages, ILogger<PackageCatalog>? logger = null, IHostApplicationLifetime? lifetime = null)
    {
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _failed = [];
        _engines = [new SourceCatalog(options, new GitHubActionsBuildSource(options, github), memoryCache, artifacts, packages,
            _logger, lifetime)];
    }

    private PackageCatalog(ProxyOptions options, List<SourceCatalog> engines, List<FailedSource> failed, ILogger logger)
    {
        _options = options;
        _engines = engines;
        _failed = failed;
        _logger = logger;
    }

    internal static PackageCatalog Create(ProxyOptions root, IBuildSourceFactory factory, IMemoryCache memoryCache,
        ILogger<PackageCatalog> logger, ILogger<ArtifactCache>? artifactLogger = null, ILogger<PackageCache>? packageLogger = null,
        IHostApplicationLifetime? lifetime = null)
    {
        var sources = root.CreateSourceOptions();
        if (sources.Count == 0)
        {
            var empty = new ProxyOptions { HasSource = false, CacheDirectory = root.CacheDirectory, BaseUrl = root.BaseUrl };
            var artifacts = new ArtifactCache(empty, new GitHubActionsBuildSource(empty, new NoGitHubClient()));
            return new PackageCatalog(root, [new SourceCatalog(empty, new GitHubActionsBuildSource(empty, new NoGitHubClient()),
                memoryCache, artifacts, new PackageCache(empty, artifacts), logger, lifetime)], [], logger);
        }

        CacheLayout.MigrateLegacy(root.CacheDirectory, sources, logger);
        var engines = new List<SourceCatalog>();
        var failed = new List<FailedSource>();
        foreach (var options in sources)
        {
            try
            {
                var source = factory.Create(options);
                var artifacts = new ArtifactCache(options, source, artifactLogger);
                var packages = new PackageCache(options, artifacts, packageLogger,
                    independentArtifactCaches: true);
                engines.Add(new SourceCatalog(options, source, memoryCache, artifacts, packages, logger, lifetime));
            }
            catch (Exception exception) when (sources.Count > 1)
            {
                logger.LogError(exception, "Source '{Source}' could not be started and will be unavailable: {Error}",
                    options.Name, exception.Message);
                failed.Add(new FailedSource(options.Name, options.Repository, exception));
            }
        }
        if (engines.Count == 0)
            ExceptionDispatchInfo.Capture(failed[0].Error).Throw();
        return new PackageCatalog(root, engines, failed, logger);
    }

    // Lets the empty-configuration catalog exist without ever contacting GitHub.
    private sealed class NoGitHubClient : IGitHubClient
    {
        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public async Task<PublishedPackage[]> GetPackagesAsync(CancellationToken cancellationToken)
    {
        if (Single) return Stamp(_engines[0], await _engines[0].GetPackagesAsync(cancellationToken));
        var results = await RunAsync(engine => engine.GetPackagesAsync(cancellationToken), cancellationToken);
        var merged = new List<PublishedPackage>();
        var seen = new Dictionary<string, string>();
        foreach (var (engine, packages) in results)
        {
            foreach (var package in Stamp(engine, packages))
            {
                var key = package.Id.ToLowerInvariant() + "|" + NuGetVersion.Parse(package.Build.Version).ToNormalizedString();
                if (seen.TryGetValue(key, out var owner))
                {
                    LogCollision(key, owner, engine.Name);
                    continue;
                }
                seen[key] = engine.Name;
                merged.Add(package);
            }
        }
        return merged.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => NuGetVersion.Parse(x.Build.Version), VersionComparer.VersionRelease).ToArray();
    }

    public async Task<BuildEntry[]> GetBuildsAsync(CancellationToken cancellationToken)
    {
        if (Single) return Stamp(_engines[0], await _engines[0].GetBuildsAsync(cancellationToken));
        var results = await RunAsync(engine => engine.GetBuildsAsync(cancellationToken), cancellationToken);
        return results.SelectMany(x => Stamp(x.Engine, x.Result))
            .OrderBy(x => SourceCatalog.BuildVersion(x), PackageRewriter.VersionOrder).ToArray();
    }

    public async Task<BuildEntry[]> GetScriptBuildsAsync(CancellationToken cancellationToken)
    {
        if (Single) return Stamp(_engines[0], await _engines[0].GetScriptBuildsAsync(cancellationToken));
        var results = await RunAsync(engine => engine.GetScriptBuildsAsync(cancellationToken), cancellationToken);
        var merged = new List<BuildEntry>();
        var seen = new HashSet<string>();
        foreach (var (engine, builds) in results)
        {
            foreach (var build in Stamp(engine, builds))
            {
                if (!seen.Add(NuGetVersion.Parse(build.Version).ToNormalizedString()))
                {
                    LogCollision("build|" + build.Version, "an earlier source", engine.Name);
                    continue;
                }
                merged.Add(build);
            }
        }
        return merged.OrderBy(x => NuGetVersion.Parse(x.Version), VersionComparer.VersionRelease).ToArray();
    }

    public async Task<RunArtifacts[]> GetRunArtifactsAsync(CancellationToken cancellationToken)
    {
        if (Single) return await _engines[0].GetRunArtifactsAsync(cancellationToken);
        var results = new List<RunArtifacts>();
        var errors = new List<string>();
        foreach (var failed in _failed) errors.Add($"[{failed.Name}] {DiscoveryFailure.Describe(failed.Error)}");
        foreach (var engine in _engines)
        {
            try
            {
                results.AddRange(await engine.GetRunArtifactsAsync(cancellationToken));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"[{engine.Name}] {engine.ReportWarmupFailure(exception)}");
            }
        }
        if (results.Count == 0 && errors.Count >= _engines.Count + _failed.Count)
            throw new DiscoveryUnavailableException(string.Join(" ", errors));
        return results.ToArray();
    }

    public async Task<string> GetPackagePathAsync(string id, BuildEntry build, CancellationToken cancellationToken)
    {
        var engine = EngineFor(build);
        try
        {
            return await engine.Packages.GetAsync(id, build, cancellationToken);
        }
        catch (Exception exception)
        {
            try { exception.Data[SourceKey] = engine.Name; } catch (Exception) { }
            throw;
        }
    }

    public string RepositoryFor(BuildEntry build) => EngineFor(build).Options.Repository;

    internal PackageCache GetPackageCache(string? name = null) => Engine(name).Packages;
    internal ArtifactCache GetArtifactCache(string? name = null) => Engine(name).Artifacts;
    internal IReadOnlyList<string> SourceNames => _engines.Select(x => x.Name).ToArray();
    internal IReadOnlyList<SourceCatalog> Sources => _engines.Where(x => x.Options.HasSource).ToArray();

    private SourceCatalog Engine(string? name) => name is null ? _engines.Single() : _engines.First(x => x.Name == name);

    private SourceCatalog EngineFor(BuildEntry build) =>
        _engines.FirstOrDefault(x => x.Name == build.SourceName) ?? _engines[0];

    private static BuildEntry[] Stamp(SourceCatalog engine, BuildEntry[] builds) =>
        builds.Select(x => x with { SourceName = engine.Name }).ToArray();

    private static PublishedPackage[] Stamp(SourceCatalog engine, PublishedPackage[] packages) =>
        packages.Select(x => x with { Build = x.Build with { SourceName = engine.Name } }).ToArray();

    private void LogCollision(string key, string first, string second)
    {
        lock (_loggedCollisions)
            if (!_loggedCollisions.Add(key)) return;
        _logger.LogWarning(
            "Package '{Key}' is published by more than one source ('{First}' and '{Second}'). The first source wins; the other is hidden.",
            key, first, second);
    }

    // Runs an operation against every source that has not failed in this request; throws only when none succeeded.
    private async Task<List<(SourceCatalog Engine, T Result)>> RunAsync<T>(
        Func<SourceCatalog, Task<T>> operation, CancellationToken cancellationToken)
    {
        var scope = _scope.Value;
        var results = new List<(SourceCatalog, T)>();
        var errors = new List<string>();
        foreach (var failed in _failed) errors.Add($"[{failed.Name}] {failed.Error.Message}");
        if (_failed.Count > 0 && scope is not null) scope.Partial = true;
        foreach (var engine in _engines)
        {
            if (scope is not null && scope.Failed.TryGetValue(engine, out var previous))
            {
                errors.Add($"[{engine.Name}] {DiscoveryFailure.Describe(previous)}");
                continue;
            }
            try
            {
                results.Add((engine, await operation(engine)));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Source '{Source}' is unavailable: {Error}", engine.Name, exception.Message);
                scope?.Failed[engine] = exception;
                if (scope is not null) scope.Partial = true;
                errors.Add($"[{engine.Name}] {DiscoveryFailure.Describe(exception)}");
            }
        }
        if (results.Count == 0) throw new DiscoveryUnavailableException(string.Join(" ", errors));
        return results;
    }

    internal IDisposable BeginFeedRequest()
    {
        var inner = _engines.Select(x => x.BeginFeedRequest()).ToArray();
        var scope = new AggregateScope(this, _scope.Value, inner);
        _scope.Value = scope;
        return scope;
    }

    internal async Task<SourceCatalog.FeedAvailability> EnsureFeedAvailableAsync(CancellationToken cancellationToken)
    {
        if (Single) return await _engines[0].EnsureFeedAvailableAsync(cancellationToken);
        var results = await RunAsync(engine => engine.EnsureFeedAvailableAsync(cancellationToken), cancellationToken);
        var availability = Merge(results.Select(x => x.Result), _scope.Value?.Partial == true || results.Count < _engines.Count + _failed.Count);
        if (_scope.Value is { } scope) scope.Availability = availability;
        return availability;
    }

    internal Func<SourceCatalog.FeedAvailability> CaptureRequestAvailability()
    {
        if (Single) return _engines[0].CaptureRequestAvailability();
        var scope = _scope.Value;
        var captures = _engines.Select(x => x.CaptureRequestAvailability()).ToArray();
        return () => Merge(captures.Select(x => x()), scope?.Partial == true || _failed.Count > 0);
    }

    private static readonly string[] MembershipOrder = ["current", "stale", "unknown"];
    private static readonly string[] ProvenanceOrder = ["verified", "legacy-unverified", "unknown", "incomplete"];

    private static SourceCatalog.FeedAvailability Merge(IEnumerable<SourceCatalog.FeedAvailability> items, bool partial)
    {
        var list = items.ToList();
        var degraded = partial || list.Any(x => x.IsDegraded);
        partial |= list.Any(x => x.IsPartial);
        string Worst(IEnumerable<string> values, string[] order) =>
            values.OrderByDescending(x => Math.Max(Array.IndexOf(order, x), 0)).FirstOrDefault() ?? order[0];
        var membership = Worst(list.Select(x => x.Membership), MembershipOrder);
        var provenance = Worst(list.Select(x => x.Provenance), ProvenanceOrder);
        if (partial && membership == "current") membership = "stale";
        return new SourceCatalog.FeedAvailability(degraded, partial, membership, provenance);
    }

    private bool HasFailedSources => _failed.Count > 0 || _scope.Value?.Failed.Count > 0 || _scope.Value?.Partial == true;

    private static string FailureText(IEnumerable<string> names) => "Source(s) " + string.Join(", ", names.Select(x => $"'{x}'"))
        + " are unavailable, so the feed is incomplete.";

    private void ThrowIfSourcesFailed()
    {
        if (!HasFailedSources) return;
        var names = _failed.Select(x => x.Name).Concat(_scope.Value?.Failed.Keys.Select(x => x.Name) ?? []).Distinct();
        throw new DiscoveryUnavailableException(FailureText(names));
    }

    internal void EnsurePackageIndexComplete(string id, bool hasVersions)
    {
        if (Single) { _engines[0].EnsurePackageIndexComplete(id, hasVersions); return; }
        ThrowIfSourcesFailed();
        foreach (var engine in Healthy()) engine.EnsurePackageIndexComplete(id, hasVersions);
    }

    internal void EnsureLookupComplete()
    {
        if (Single) { _engines[0].EnsureLookupComplete(); return; }
        ThrowIfSourcesFailed();
        foreach (var engine in Healthy()) engine.EnsureLookupComplete();
    }

    internal void EnsureAggregateComplete()
    {
        if (Single) { _engines[0].EnsureAggregateComplete(); return; }
        ThrowIfSourcesFailed();
        foreach (var engine in Healthy()) engine.EnsureAggregateComplete();
    }

    private IEnumerable<SourceCatalog> Healthy() =>
        _engines.Where(x => _scope.Value?.Failed.ContainsKey(x) != true);

    internal string ReportWarmupFailure(Exception exception)
    {
        if (Single) return _engines[0].ReportWarmupFailure(exception);
        return DiscoveryFailure.Describe(exception);
    }

    internal string ReportLateContentFailure(Exception exception)
    {
        if (Single) return _engines[0].ReportLateContentFailure(exception);
        var name = exception.Data[SourceKey] as string;
        var engine = _engines.FirstOrDefault(x => x.Name == name);
        return engine?.ReportLateContentFailure(exception) ?? DiscoveryFailure.DescribeContent(exception);
    }

    public CatalogHealth GetHealth()
    {
        if (Single) return _engines[0].GetHealth() with { Source = _engines[0].Name.Length == 0 ? null : _engines[0].Name };
        var sources = _engines.Select(x => x.GetHealth() with { Source = x.Name }).ToList();
        foreach (var failed in _failed)
            sources.Add(new CatalogHealth("degraded", failed.Repository, failed.Error.Message, null, "unknown", 0,
                null, null, null, null, "unknown", failed.Name));
        var bad = sources.Where(x => x.Status != "ok").ToList();
        return new CatalogHealth(
            bad.Count == 0 ? "ok" : "degraded",
            string.Join(", ", sources.Select(x => x.Repository).Where(x => x.Length > 0)),
            bad.Count == 0 ? null : string.Join(" ", bad.Select(x => $"[{x.Source}] {x.Error}")),
            sources.Where(x => x.LastSuccessfulRefreshUtc is not null).Select(x => x.LastSuccessfulRefreshUtc).Min(),
            Worse(sources.Select(x => x.Membership), MembershipOrder),
            sources.Sum(x => x.SnapshotBuilds),
            Sum(sources.Select(x => x.AvailableCachedBuilds)),
            Sum(sources.Select(x => x.UnavailableBuilds)),
            Sum(sources.Select(x => x.UnavailablePackages)),
            Sum(sources.Select(x => x.UnavailableBuildMetadata)),
            Worse(sources.Select(x => x.Provenance), ProvenanceOrder),
            null,
            sources);

        static int? Sum(IEnumerable<int?> values) => values.Any(x => x is not null) ? values.Sum(x => x ?? 0) : null;
        static string Worse(IEnumerable<string> values, string[] order) =>
            values.OrderByDescending(x => Math.Max(Array.IndexOf(order, x), 0)).FirstOrDefault() ?? order[0];
    }

    internal long BackgroundWorkStarted => _engines.Sum(x => x.BackgroundWorkStarted);

    internal async Task<bool> WaitForBackgroundWorkAsync(CancellationToken cancellationToken)
    {
        var waited = false;
        foreach (var engine in _engines) waited |= await engine.WaitForBackgroundWorkAsync(cancellationToken);
        return waited;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines) await engine.DisposeAsync();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
