using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace Keboo.LocalCache;

public sealed record SourceStartFailure(string Name, string Error);

// What a reconciliation did: names of sources added, updated (cache purged), removed, and whose cache directory was
// purged (changed sources plus removed sources the caller asked to purge); Failed lists sources that could not start.
public sealed record SourceReconciliation(
    IReadOnlyList<string> Added, IReadOnlyList<string> Updated, IReadOnlyList<string> Removed,
    IReadOnlyList<string> CachePurged, IReadOnlyList<SourceStartFailure> Failed)
{
    public static SourceReconciliation None { get; } = new([], [], [], [], []);
}

// Aggregates one SourceCatalog per configured build source. With a single healthy source every call is delegated unchanged.
// With several sources a failing source never takes down the others, but its absence is never served as a complete feed.
public sealed class PackageCatalog : IDisposable, IAsyncDisposable
{
    private const string SourceKey = "127cache.source";

    private sealed record FailedSource(string Name, string Repository, Exception Error);

    private sealed class AggregateScope(PackageCatalog catalog, AggregateScope? previous, State state, IDisposable[] inner) : IDisposable
    {
        public State State { get; } = state;
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
    private sealed record State(IReadOnlyList<SourceCatalog> Engines, IReadOnlyList<FailedSource> Failed);

    // Replaced as a whole when sources are reconciled, so a request always sees a consistent set of sources.
    private State _state;
    private readonly object _stateGate = new();
    private State CurrentState => _scope.Value?.State ?? Volatile.Read(ref _state);
    private IReadOnlyList<SourceCatalog> _engines => CurrentState.Engines;
    private IReadOnlyList<FailedSource> _failed => CurrentState.Failed;
    private readonly Func<ProxyOptions, SourceCatalog>? _createEngine;
    private readonly Func<SourceCatalog>? _createPlaceholder;
    private readonly SemaphoreSlim _reconcileGate = new(1);
    private bool _disposed;
    private readonly ILogger _logger;
    private readonly AsyncLocal<AggregateScope?> _scope = new();
    private readonly HashSet<string> _loggedCollisions = [];
    private Task _startup = Task.CompletedTask;
    internal void SetStartup(Task startup) => Volatile.Write(ref _startup, startup);
    internal Task WaitForStartupAsync(CancellationToken token) => Volatile.Read(ref _startup).WaitAsync(token);
    internal event Action? Changed;
    private void OnPublishedChanged() => Changed?.Invoke();
    private bool Single => CurrentState is { Engines.Count: 1, Failed.Count: 0 };

    // Wraps one GitHub Actions source that owns the cache directory of `options` directly.
    public PackageCatalog(IOptions<ProxyOptions> proxyOptions, IGitHubClient github, IMemoryCache memoryCache, ArtifactCache artifacts,
        PackageCache packages, ILogger<PackageCatalog>? logger = null, IHostApplicationLifetime? lifetime = null)
    {
        var options = proxyOptions.Value;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _state = new State([new SourceCatalog(options, new GitHubActionsBuildSource(options, github), memoryCache, artifacts, packages,
            _logger, lifetime)], []);
        _state.Engines[0].PublishedChanged += OnPublishedChanged;
    }

    private PackageCatalog(ProxyOptions options, List<SourceCatalog> engines, List<FailedSource> failed, ILogger logger,
        Func<ProxyOptions, SourceCatalog>? createEngine = null, Func<SourceCatalog>? createPlaceholder = null)
    {
        _options = options;
        _state = new State(engines, failed);
        foreach (var engine in engines) engine.PublishedChanged += OnPublishedChanged;
        _logger = logger;
        _createEngine = createEngine;
        _createPlaceholder = createPlaceholder;
    }

    internal static PackageCatalog Create(IOptions<ProxyOptions> proxyOptions, IBuildSourceFactory factory, IMemoryCache memoryCache,
        ILogger<PackageCatalog> logger, ILogger<ArtifactCache>? artifactLogger = null, ILogger<PackageCache>? packageLogger = null,
        IHostApplicationLifetime? lifetime = null)
    {
        var root = proxyOptions.Value;
        SourceCatalog CreateEngine(ProxyOptions options)
        {
            var source = factory.Create(options);
            var sourceOptions = Options.Create(options);
            var artifacts = new ArtifactCache(sourceOptions, source, artifactLogger);
            var packages = new PackageCache(sourceOptions, artifacts, packageLogger, independentArtifactCaches: true);
            return new SourceCatalog(options, source, memoryCache, artifacts, packages, logger, lifetime);
        }

        SourceCatalog Placeholder() => CreatePlaceholder(root, memoryCache, logger, lifetime);
        var sources = root.CreateSourceOptions();
        if (sources.Count == 0)
            return new PackageCatalog(root, [Placeholder()], [], logger, CreateEngine, Placeholder);

        CacheLayout.MigrateLegacy(root.CacheDirectory, sources, logger);
        var engines = new List<SourceCatalog>();
        var failed = new List<FailedSource>();
        foreach (var options in sources)
        {
            try
            {
                engines.Add(CreateEngine(options));
            }
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, CancellationToken.None))
            {
                logger.LogError(exception, "Source '{Source}' could not be started and will be unavailable: {Error}",
                    options.Name, exception.Message);
                failed.Add(new FailedSource(options.Name, options.Repository, exception));
            }
        }
        if (engines.Count == 0) engines.Add(Placeholder());
        return new PackageCatalog(root, engines, failed, logger, CreateEngine, Placeholder);
    }

    // The catalog of an empty configuration: it never contacts GitHub and serves an empty feed.
    private static SourceCatalog CreatePlaceholder(ProxyOptions root, IMemoryCache memoryCache, ILogger logger,
        IHostApplicationLifetime? lifetime)
    {
        var empty = new ProxyOptions { HasSource = false, CacheDirectory = root.CacheDirectory, BaseUrl = root.BaseUrl };
        var emptyOptions = Options.Create(empty);
        var artifacts = new ArtifactCache(emptyOptions, new GitHubActionsBuildSource(empty, new NoGitHubClient()));
        return new SourceCatalog(empty, new GitHubActionsBuildSource(empty, new NoGitHubClient()),
            memoryCache, artifacts, new PackageCache(emptyOptions, artifacts), logger, lifetime);
    }

    // Applies a new list of sources to the running catalog without a restart. Unchanged sources keep their engine (and
    // their in-memory state); removed and changed sources are disposed. A changed source (repository, workflow or
    // artifacts) has its cache directory purged because every cached run, package and rewritten version belongs to the
    // old definition. A removed source keeps its cache directory unless purgeRemoved names it; that is the caller's choice.
    internal async Task<SourceReconciliation> ReconcileAsync(IReadOnlyList<SourceOptions> sources,
        IReadOnlyCollection<string> purgeRemoved, CancellationToken cancellationToken = default)
    {
        if (_createEngine is null || _createPlaceholder is null)
            throw new NotSupportedException("This catalog was not created from configuration and cannot reconcile sources.");
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = Volatile.Read(ref _state);
            var previousSources = _options.ConfiguredSources.ToArray();
            _options.ApplySources(sources);
            var desired = _options.CreateSourceOptions();
            var kept = new Dictionary<string, SourceCatalog>(StringComparer.Ordinal);
            var added = new List<string>();
            var updated = new List<string>();
            var purged = new List<string>();
            var retired = new List<SourceCatalog>();
            foreach (var engine in previous.Engines.Where(x => x.Options.HasSource))
            {
                var next = desired.FirstOrDefault(x => x.Name == engine.Name);
                if (next is null)
                {
                    retired.Add(engine);
                    if (purgeRemoved.Contains(engine.Name, StringComparer.OrdinalIgnoreCase)) purged.Add(engine.Name);
                }
                else if (engine.Options.Repository == next.Repository && engine.Options.Workflow == next.Workflow
                    && engine.Options.PackagesArtifact == next.PackagesArtifact
                    && engine.Options.VersionArtifact == next.VersionArtifact)
                    kept[engine.Name] = engine;
                else
                {
                    retired.Add(engine);
                    updated.Add(engine.Name);
                    purged.Add(engine.Name);
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                _options.ApplySources(previousSources);
                cancellationToken.ThrowIfCancellationRequested();
            }
            lock (_stateGate)
                Volatile.Write(ref _state, new State(previous.Engines.Except(retired).ToArray(), previous.Failed));
            foreach (var engine in retired)
            {
                engine.PublishedChanged -= OnPublishedChanged;
                await engine.DisposeAsync();
                await engine.DrainPublishedAsync(CancellationToken.None);
            }
            foreach (var name in purged)
                await Task.Run(() => CacheDisk.Purge(CacheLayout.SourceDirectory(_options.CacheDirectory, name)), CancellationToken.None);

            var engines = new List<SourceCatalog>();
            var failed = new List<FailedSource>();
            var started = new List<SourceCatalog>();
            foreach (var options in desired)
            {
                if (kept.TryGetValue(options.Name, out var existing))
                {
                    engines.Add(existing);
                    continue;
                }
                if (!updated.Contains(options.Name)) added.Add(options.Name);
                try
                {
                    var engine = _createEngine(options);
                    engine.PublishedChanged += OnPublishedChanged;
                    engines.Add(engine);
                    started.Add(engine);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, CancellationToken.None))
                {
                    _logger.LogError(exception, "Source '{Source}' could not be started and will be unavailable: {Error}",
                        options.Name, exception.Message);
                    failed.Add(new FailedSource(options.Name, options.Repository, exception));
                }
            }
            if (engines.Count == 0)
            {
                var placeholder = _createPlaceholder();
                placeholder.PublishedChanged += OnPublishedChanged;
                engines.Add(placeholder);
            }
            lock (_stateGate) Volatile.Write(ref _state, new State(engines, failed));
            lock (_loggedCollisions) _loggedCollisions.Clear();
            foreach (var engine in started) engine.StartForcedRefresh();
            Changed?.Invoke();
            return new SourceReconciliation(added, updated,
                retired.Select(x => x.Name).Where(x => !updated.Contains(x)).ToArray(), purged,
                failed.Select(x => new SourceStartFailure(x.Name, x.Error.Message)).ToArray());
        }
        finally
        {
            _reconcileGate.Release();
        }
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
        if (_engines.Count == 0) return [];
        if (Single)
        {
            var only = _engines[0];
            return Stamp(only, await only.GetPackagesAsync(cancellationToken));
        }
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
        if (Single)
        {
            var only = _engines[0];
            return Stamp(only, await only.GetBuildsAsync(cancellationToken));
        }
        var results = await RunAsync(engine => engine.GetBuildsAsync(cancellationToken), cancellationToken);
        return results.SelectMany(x => Stamp(x.Engine, x.Result))
            .OrderBy(x => SourceCatalog.BuildVersion(x), PackageRewriter.VersionOrder).ToArray();
    }

    public async Task<BuildEntry[]> GetScriptBuildsAsync(CancellationToken cancellationToken)
    {
        if (_engines.Count == 0) return [];
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
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
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
        if (_scope.Value is not null) return engine.PublishedPath(id, build);
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
    internal IReadOnlyList<(string Name, string Repository, string Error)> FailedSources =>
        _failed.Select(x => (x.Name, x.Repository, x.Error.Message)).ToArray();

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
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
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
        lock (_stateGate)
        {
            var state = Volatile.Read(ref _state);
            var inner = state.Engines.Select(x => x.BeginFeedRequest()).ToArray();
            var scope = new AggregateScope(this, _scope.Value, state, inner);
            _scope.Value = scope;
            return scope;
        }
    }

    internal async Task<SourceCatalog.FeedAvailability> EnsureFeedAvailableAsync(CancellationToken cancellationToken)
    {
        if (_engines.Count == 0) return new SourceCatalog.FeedAvailability(true, true, "unknown", "unknown");
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
        if (_scope.Value is not null) return;
        if (Single) { _engines[0].EnsurePackageIndexComplete(id, hasVersions); return; }
        ThrowIfSourcesFailed();
        foreach (var engine in Healthy()) engine.EnsurePackageIndexComplete(id, hasVersions);
    }

    internal void EnsureLookupComplete()
    {
        if (_scope.Value is not null) return;
        if (Single) { _engines[0].EnsureLookupComplete(); return; }
        ThrowIfSourcesFailed();
        foreach (var engine in Healthy()) engine.EnsureLookupComplete();
    }

    internal void EnsureAggregateComplete()
    {
        if (_scope.Value is not null) return;
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
        await _reconcileGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var engine in _engines) await engine.DisposeAsync();
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
