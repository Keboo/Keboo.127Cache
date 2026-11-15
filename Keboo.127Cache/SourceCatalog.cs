using System.IO.Compression;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;

using NuGet.Versioning;

namespace Keboo.LocalCache;

public sealed record BuildEntry(
    long RunId, int Attempt, int PullRequest, string HeadSha, string OriginalVersion, string Version,
    Artifact Packages, Artifact? VersionArtifact, string SourceName = "");
public sealed record CatalogFile(string Repository, List<BuildEntry> Builds, int SchemaVersion = 1);
public sealed record PublishedPackage(string Id, BuildEntry Build);
public sealed record RunArtifacts(
    long RunId, int Attempt, int PullRequest, string HeadSha, Artifact Packages, Artifact? VersionArtifact,
    bool WorkflowVerified = false);
public sealed record RunArtifactsFile(string Repository, string Workflow, List<RunArtifacts> Runs, int SchemaVersion = 1);

internal sealed partial class SourceCatalog : IDisposable, IAsyncDisposable
{
    private readonly ProxyOptions _options;
    private readonly IBuildSource _source;
    private readonly ILogger _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly ArtifactCache _artifacts;
    private readonly PackageCache _packages;
    private readonly object _cacheKey = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _refreshGate = new();
    private readonly object _contentRefreshGate = new();
    private readonly object _buildRefreshGate = new();
    private readonly object _offlineVersionGate = new();
    private readonly object _incompleteGate = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundWork = [];
    private long _backgroundWorkStarted;
    private readonly CancellationTokenSource _shutdown;
    private bool _disposed;
    private readonly Dictionary<long, Task<string>> _offlineVersionReads = [];
    private readonly CancellationToken _stopping;
    private readonly string _file;
    private readonly string _runsFile;
    private readonly string _snapshotFile;
    private readonly string _incompleteFile;
    private readonly object _healthGate = new();
    private readonly AsyncLocal<FeedRequestScope?> _requestScope = new();
    private List<RunArtifacts> _runs;
    private List<BuildEntry> _builds;
    private OfflineSnapshot? _snapshot;
    private string? _snapshotError;
    private CatalogHealth _health;
    private string? _contentError;
    private string? _pinnedWorkflowError;
    private bool _catalogUnreadable;
    private bool _runsUnreadable;
    private volatile bool _incompleteUnreadable;
    private volatile bool _incompleteRetryable;
    private long _incompleteGeneration;
    private Task<Discovery>? _refreshTask;
    private PartialDiscovery? _partialDiscovery;
    private ContentRefresh? _contentRefresh;
    private BuildRefresh? _buildRefresh;
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OfflineWait = TimeSpan.FromMilliseconds(500);
    private const string ContentPending = "Build content is still being prepared; cached results may be incomplete.";

    private sealed record Discovery(
        RunArtifacts[] Runs, bool IsStale, string? Error, OfflineSnapshot? Snapshot,
        bool MembershipUnknown = false, int SkippedBuilds = 0, bool MetadataIncomplete = false,
        bool UnverifiedPinnedRuns = false, bool PersistencePending = false)
    {
        public bool IsPartial => SkippedBuilds > 0 || MetadataIncomplete;
        public string Membership => MembershipUnknown ? "unknown" : IsStale ? "stale" : "current";
        public string Provenance => UnverifiedPinnedRuns ? "incomplete" : ProvenanceFor(Runs);
    }

    private sealed record OfflineResolution(
        PublishedPackage[] Packages, BuildEntry[] Builds, int AvailableBuilds, int UnavailableBuilds,
        int UnavailablePackages, int UnavailableBuildMetadata, bool UnknownInventory, HashSet<string> MissingPackageIds,
        bool DiscoveryIncomplete, bool VerificationPending)
    {
        public bool IsPartial => DiscoveryIncomplete || UnavailableBuilds > 0 || UnavailablePackages > 0 || UnavailableBuildMetadata > 0;
    }

    private sealed record OnlineBuilds(BuildEntry[] Builds, Exception? Failure);
    private sealed record OnlinePackages(PublishedPackage[] Packages, Exception? Failure);
    private sealed record OfflineRun(BuildEntry? Build, CachedPackageAvailability Availability, bool VerificationPending = false);
    private sealed record PartialDiscovery(OfflineSnapshot Configuration, Discovery Discovery);
    private sealed record IncompleteDiscoveryFile(
        int SchemaVersion, string Repository, string Workflow, string PackagesArtifact, string VersionArtifact,
        int[] PullRequests, long[] RunIds, int RunLimit, RunArtifacts[] Runs, int SkippedBuilds,
        bool MetadataIncomplete, bool UnverifiedPinnedRuns);
    private sealed record ContentRefresh(RunArtifacts[] Runs, Task<OnlinePackages> Work);
    private sealed record BuildRefresh(RunArtifacts[] Runs, Task<OnlineBuilds> Work);

    internal sealed record FeedAvailability(bool IsDegraded, bool IsPartial, string Membership, string Provenance);

    private sealed class FeedRequestScope(SourceCatalog catalog, FeedRequestScope? previous) : IDisposable
    {
        public Discovery? Discovery { get; set; }
        public OfflineResolution? Offline { get; set; }
        public bool ContentPending { get; set; }
        public FeedAvailability Availability { get; set; } = new(false, false, "current", "unknown");
        public void Dispose() => catalog._requestScope.Value = previous;
    }
    private bool _catalogNeedsUpgrade;

    internal string Name => _options.Name;
    internal ProxyOptions Options => _options;
    internal PackageCache Packages => _packages;
    internal ArtifactCache Artifacts => _artifacts;

    public SourceCatalog(ProxyOptions options, IBuildSource source, IMemoryCache memoryCache, ArtifactCache artifacts, PackageCache packages,
        ILogger? logger = null, IHostApplicationLifetime? lifetime = null)
    {
        _options = options;
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.ApplicationStopping ?? CancellationToken.None);
        _stopping = _shutdown.Token;
        _memoryCache = memoryCache;
        _artifacts = artifacts;
        _packages = packages;
        _file = Path.Combine(options.CacheDirectory, "catalog.json");
        _runsFile = Path.Combine(options.CacheDirectory, "runs.json");
        Directory.CreateDirectory(options.CacheDirectory);
        CacheDisk.SweepTrash(options.CacheDirectory);
        _closedFile = Path.Combine(options.CacheDirectory, "closed-pull-requests.json");
        LoadClosedPullRequests();
        _builds = [];
        _runs = [];
        if (options.HasSource)
        {
            LoadCatalog();
            LoadRuns();
        }
        _snapshotFile = Path.Combine(options.CacheDirectory, "visible-runs.json");
        _snapshot = OfflineSnapshot.Load(_snapshotFile, options, out _snapshotError);
        _incompleteFile = Path.Combine(options.CacheDirectory, "incomplete-discovery.json");
        LoadIncompleteDiscovery();
        var matchingSnapshot = MatchingSnapshot();
        _health = new CatalogHealth("degraded", options.Repository, "Initial GitHub discovery has not completed.",
            matchingSnapshot?.LastSuccessfulRefreshUtc, matchingSnapshot is null ? "unknown" : "stale",
            matchingSnapshot?.Runs.Length ?? 0, null, null, null, null, ProvenanceFor(matchingSnapshot?.Runs));
        if (!options.HasSource) _health = _health with { Status = "ok", Error = null };
    }

    public async Task<PublishedPackage[]> GetPackagesAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasSource) return [];
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsStale)
            return (await RequireOfflinePackagesAsync(discovery, cancellationToken)).Packages;

        try
        {
            var work = StartContentRefresh(discovery);
            if (work is not null)
            {
                if (await CompletesWithinRequestAsync(work, cancellationToken))
                {
                    var result = await work;
                    if (result.Failure is not null)
                        return (await RecoverFromContentFailureAsync(result.Failure, discovery, cancellationToken)).Packages;
                    MarkContentHealthy(discovery);
                    return result.Packages;
                }
            }
            else MarkContentPending(discovery);
            return (await ResolvePendingContentAsync(discovery, cancellationToken)).Packages;
        }
        catch (Exception exception) when (exception is not DiscoveryUnavailableException
            && DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return (await RecoverFromContentFailureAsync(exception, discovery, cancellationToken)).Packages;
        }
    }

    public async Task<BuildEntry[]> GetBuildsAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasSource) return [];
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsStale)
            return (await RequireOfflinePackagesAsync(discovery, cancellationToken)).Builds;
        try
        {
            var work = StartBuildRefresh(discovery.Runs);
            if (work is not null)
            {
                if (await CompletesWithinRequestAsync(work, cancellationToken))
                {
                    var online = await work;
                    if (online.Failure is not null)
                        return (await RecoverFromContentFailureAsync(online.Failure, discovery, cancellationToken)).Builds;
                    return online.Builds;
                }
            }
            MarkContentPending(discovery);
            return (await ResolvePendingContentAsync(discovery, cancellationToken)).Builds;
        }
        catch (Exception exception) when (exception is not DiscoveryUnavailableException
            && DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return (await RecoverFromContentFailureAsync(exception, discovery, cancellationToken)).Builds;
        }
    }

    private Task<OnlinePackages>? StartContentRefresh(Discovery discovery)
    {
        lock (_contentRefreshGate)
        {
            if (_contentRefresh is { Work.IsCompleted: false } pending)
                return pending.Runs.SequenceEqual(discovery.Runs) ? pending.Work : null;
            MarkContentPending(discovery);
            var work = TrackBackground(async () =>
            {
                var result = await MaterializePackagesAsync(discovery.Runs, _stopping);
                if (result.Failure is null) MarkContentHealthy(discovery, pendingOnly: true);
                return result;
            });
            _contentRefresh = new ContentRefresh(discovery.Runs, work);
            return work;
        }
    }

    private Task<OnlineBuilds>? StartBuildRefresh(RunArtifacts[] runs)
    {
        lock (_buildRefreshGate)
        {
            if (_buildRefresh is { Work.IsCompleted: false } pending)
                return pending.Runs.SequenceEqual(runs) ? pending.Work : null;
            var work = TrackBackground(() => GetOnlineBuildsAsync(runs, _stopping));
            _buildRefresh = new BuildRefresh(runs, work);
            return work;
        }
    }

    private async Task<OnlinePackages> MaterializePackagesAsync(RunArtifacts[] runs, CancellationToken cancellationToken)
    {
        try
        {
            var result = new List<PublishedPackage>();
            var online = await GetOnlineBuildsAsync(runs, cancellationToken);
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
                catch (Exception exception) when (IsBuildFailure(exception) && !_options.RunIds.Contains(build.RunId))
                {
                    ReportUnavailableBuild(build, exception);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken)
                    && !_options.RunIds.Contains(build.RunId))
                {
                    failure ??= exception;
                }
            }
            return new OnlinePackages(
                result.OrderBy(x => NuGetVersion.Parse(x.Build.Version), VersionComparer.VersionRelease).ToArray(), failure);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken))
        {
            return new OnlinePackages([], exception);
        }
    }

    private Task<T> TrackBackground<T>(Func<Task<T>> start)
    {
        lock (_backgroundGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SourceCatalog));
            _backgroundWorkStarted++;
            var task = start();
            _backgroundWork.Add(task);
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (_backgroundGate) _backgroundWork.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_backgroundGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _shutdown.Cancel();
        for (;;)
        {
            Task[] pending;
            lock (_backgroundGate)
            {
                _backgroundWork.RemoveWhere(task => task.IsCompleted);
                pending = _backgroundWork.ToArray();
            }
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Work failures are observed when requests consume them. */ }
        }
        _shutdown.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal long BackgroundWorkStarted
    {
        get { lock (_backgroundGate) return _backgroundWorkStarted; }
    }

    internal async Task<bool> WaitForBackgroundWorkAsync(CancellationToken cancellationToken)
    {
        var waited = false;
        for (;;)
        {
            Task[] pending;
            lock (_backgroundGate) pending = _backgroundWork.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return waited;
            waited = true;
            try { await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static async Task<bool> CompletesWithinRequestAsync(
        Task work, CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        if (work.IsCompleted) return true;
        var completed = await Task.WhenAny(work, Task.Delay(wait ?? RequestWait, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return completed == work;
    }

    private void MarkContentPending(Discovery discovery)
    {
        if (!discovery.IsPartial) SetFailureHealth(ContentPending, discovery, contentFailure: true);
    }

    private async Task<OfflineResolution> ResolvePendingContentAsync(Discovery discovery, CancellationToken cancellationToken)
    {
        var resolution = await RequireOfflinePackagesAsync(discovery with { Error = ContentPending }, cancellationToken);
        if (_requestScope.Value is { } scope)
        {
            scope.ContentPending = true;
            scope.Availability = new FeedAvailability(true, true, discovery.Membership, discovery.Provenance);
        }
        return resolution;
    }

    public async Task<BuildEntry[]> GetScriptBuildsAsync(CancellationToken cancellationToken)
    {
        var result = new List<BuildEntry>();
        foreach (var build in await GetBuildsAsync(cancellationToken))
        {
            if (build.VersionArtifact is not null)
            {
                result.Add(build);
                continue;
            }

            var tool = (await _packages.GetPackagesAsync(build, cancellationToken))
                .SingleOrDefault(x => x.Id.Equals("vpk", StringComparison.OrdinalIgnoreCase));
            if (tool is not null)
                result.Add(build with { OriginalVersion = tool.OriginalVersion, Version = tool.Version });
        }
        return result.OrderBy(x => NuGetVersion.Parse(x.Version), VersionComparer.VersionRelease).ToArray();
    }
    public async Task<RunArtifacts[]> GetRunArtifactsAsync(CancellationToken cancellationToken)
    {
        var discovery = await DiscoverAsync(cancellationToken);
        if (discovery.IsPartial && discovery.Runs.Length == 0)
            throw new DiscoveryUnavailableException(discovery.Error + " No usable builds are available.");
        return discovery.Runs;
    }

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
        var availability = new FeedAvailability(discovery.IsStale || partial, partial, discovery.Membership, discovery.Provenance);
        if (_requestScope.Value is { } scope) scope.Availability = availability;
        return availability;
    }

    internal Func<FeedAvailability> CaptureRequestAvailability()
    {
        var scope = _requestScope.Value;
        return () => scope?.Availability ?? new FeedAvailability(false, false, "current", "unknown");
    }

    internal void EnsurePackageIndexComplete(string id, bool hasVersions)
    {
        var scope = _requestScope.Value;
        if (scope?.Discovery is { SkippedBuilds: > 0 })
            throw new DiscoveryUnavailableException("A discovered build has an unknown package inventory, so package versions cannot be enumerated.");
        if (scope?.Discovery?.MetadataIncomplete == true)
            throw new DiscoveryUnavailableException("Cached build metadata is incomplete, so package versions cannot be enumerated.");
        if (scope?.Offline is { } offline
            && (offline.UnknownInventory || offline.MissingPackageIds.Contains(id)))
            throw new DiscoveryUnavailableException("A cached build may have an unavailable version of this package."
                + (scope.ContentPending ? " Build content is still being prepared." : "")
                + (offline.VerificationPending ? " Cached content verification is still in progress." : ""));
        if (!hasVersions && scope?.Availability.IsPartial == true)
            throw new DiscoveryUnavailableException("The incomplete feed cannot determine whether this package is available.");
    }

    internal void EnsureLookupComplete()
    {
        if (_requestScope.Value?.Availability.IsPartial == true)
            throw new DiscoveryUnavailableException("The incomplete feed cannot determine whether this package version is available."
                + (_requestScope.Value.ContentPending ? " Build content is still being prepared." : "")
                + (_requestScope.Value.Offline?.VerificationPending == true
                    ? " Cached content verification is still in progress." : ""));
    }

    internal void EnsureAggregateComplete()
    {
        if (_requestScope.Value?.Availability.IsPartial == true)
            throw new DiscoveryUnavailableException("The cached feed is incomplete and cannot enumerate a complete result."
                + (_requestScope.Value.ContentPending ? " Build content is still being prepared." : "")
                + (_requestScope.Value.Offline?.VerificationPending == true
                    ? " Cached content verification is still in progress." : ""));
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
            var unavailable = new HashSet<BuildEntry>();
            Exception? failure = null;
            var changed = false;
            foreach (var run in runs)
            {
                var existing = next.FindIndex(x =>
                    x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest);
                if (existing >= 0
                    && next[existing].Packages.Id == run.Packages.Id
                    && next[existing].VersionArtifact?.Id == run.VersionArtifact?.Id
                    && (run.VersionArtifact is not null
                        || (string.IsNullOrEmpty(next[existing].OriginalVersion) && string.IsNullOrEmpty(next[existing].Version))))
                    continue;
                try
                {
                    // A missing version artifact leaves no build-wide version; feed identities come from each package.
                    var build = new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, "", "",
                        run.Packages, run.VersionArtifact);
                    if (run.VersionArtifact is not null)
                    {
                        var original = await ReadVersionAsync(run.VersionArtifact, cancellationToken);
                        build = build with
                        {
                            OriginalVersion = original,
                            Version = PackageRewriter.VersionFor(original, run.PullRequest, run.RunId, run.Attempt, run.Packages.Id)
                        };
                    }
                    if (existing >= 0) next.RemoveAt(existing);
                    next.Add(build);
                    changed = true;
                }
                catch (Exception exception) when (IsBuildFailure(exception))
                {
                    _logger.LogWarning(exception, "Skipping run {RunId} attempt {Attempt} of PR {PullRequest}: " +
                        "version artifact {ArtifactId} is unavailable.", run.RunId, run.Attempt, run.PullRequest, run.VersionArtifact?.Id);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandleContent(exception, cancellationToken)
                    && !_options.RunIds.Contains(run.RunId))
                {
                    failure ??= exception;
                }
            }
            if (_catalogNeedsUpgrade)            {
                var ready = true;
                foreach (var build in next)
                {
                    try
                    {
                        await _packages.VerifyForMigrationAsync(build, cancellationToken);
                    }
                    catch (Exception exception) when (IsBuildFailure(exception))
                    {
                        ready = false;
                        unavailable.Add(build);
                        ReportUnavailableBuild(build, exception);
                    }
                }
                if (ready)
                {
                    await JsonSettings.WriteAtomicAsync(_file, new CatalogFile(_options.Repository, next, CacheSchema.Version), cancellationToken);
                    _catalogNeedsUpgrade = false;
                }
            }
            else if (changed)
                await JsonSettings.WriteAtomicAsync(_file, new CatalogFile(_options.Repository, next, CacheSchema.Version), cancellationToken);
            _builds = next;
            var visible = runs.Select(x => (x.RunId, x.Attempt, x.PullRequest)).ToHashSet();
            var candidates = _builds.Where(x => visible.Contains((x.RunId, x.Attempt, x.PullRequest)) && !unavailable.Contains(x))
                .OrderBy(x => BuildVersion(x), PackageRewriter.VersionOrder).ToArray();
            var available = new List<BuildEntry>(candidates.Length);
            foreach (var build in candidates)
            {
                try
                {
                    await _packages.GetPackagesAsync(build, cancellationToken);
                    available.Add(build);
                }
                catch (Exception exception) when (IsBuildFailure(exception))
                {
                    ReportUnavailableBuild(build, exception);
                }
            }
            available.Sort((left, right) =>
                PackageRewriter.VersionOrder.Compare(BuildVersion(left), BuildVersion(right)));
            return new OnlineBuilds(available.ToArray(), failure);        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Discovery> DiscoverAsync(CancellationToken cancellationToken)
    {
        var scope = _requestScope.Value;
        if (!_options.HasSource) return Remember(scope, new Discovery([], false, null, null));
        if (scope?.Discovery is { } requestDiscovery) return requestDiscovery;
        if (_memoryCache.TryGetValue(_cacheKey, out RunArtifacts[]? cached) && cached is not null)
            return Remember(scope, new Discovery(cached, false, null, MatchingSnapshot()));

        var refresh = StartRefresh();
        if (!refresh.IsCompleted)
        {
            var fallback = FindFallback(GetHealth().Error ?? "GitHub discovery is in progress.");
            if (fallback is not null && fallback.Runs.Any(_packages.MightHaveCachedContent))
            {
                if (fallback.PersistencePending && await CompletesWithinRequestAsync(refresh, cancellationToken, OfflineWait))
                    return Remember(scope, await refresh.WaitAsync(cancellationToken));
                return Remember(scope, fallback);
            }
            if (!await CompletesWithinRequestAsync(refresh, cancellationToken))
            {
                var error = Volatile.Read(ref _pinnedWorkflowError)
                    ?? "GitHub discovery is still in progress. Retry when it completes.";
                SetFailureHealth(error, fallback);
                throw new DiscoveryUnavailableException(error);
            }
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
            var task = TrackBackground(() => RefreshAndHandleFailureAsync(_stopping));
            return _refreshTask = task;
        }
    }

    private async Task<Discovery> RefreshAndHandleFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshAsync(cancellationToken);

        }
        catch (Exception exception) when (!exception.Data.Contains("config") && DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            var error = DiscoveryFailure.Describe(exception);
            if (exception is PinnedRunWorkflowException)
            {
                Volatile.Write(ref _pinnedWorkflowError, error);
                _memoryCache.Remove(_cacheKey);
                var blocked = new Discovery([], true, error, MatchingSnapshot(),
                    MetadataIncomplete: true, UnverifiedPinnedRuns: true);
                try
                {
                    await PersistIncompleteAsync(blocked, cancellationToken);
                }
                catch (SnapshotPersistenceException)
                {
                    error += " Could not save the incomplete discovery state.";
                }
                SetFailureHealth(error, blocked);
                _logger.LogWarning("Catalog discovery failed: {Error}", error);
                throw new DiscoveryUnavailableException(error);
            }
            if (Volatile.Read(ref _pinnedWorkflowError) is { } pinnedError
                && !error.Contains(pinnedError, StringComparison.Ordinal))
                error += " " + pinnedError;
            var snapshot = MatchingSnapshot();
            var fallback = FindFallback(error);
            if (fallback is null && snapshot is null)
                error += " " + (_snapshotError ?? "No matching successful discovery snapshot is available.");
            else if (fallback is null && snapshot?.Runs.Length == 0)
                error += " The last successful snapshot contained no builds, so no offline feed is available.";
            if (fallback is null && _incompleteUnreadable)
                error += " Previously incomplete discovery metadata cannot be read; a complete live refresh is required.";
            if (fallback is null && HasUnverifiedPinnedRuns())
                error += " Legacy pinned runs cannot be verified against the configured workflow while offline.";
            if (fallback is not null) error = fallback.Error!;
            SetFailureHealth(error, fallback);
            _logger.LogWarning("Catalog discovery failed: {Error}", error);
            return fallback ?? throw new DiscoveryUnavailableException(error);
        }
    }

    private void ValidateCachedArtifacts(RunArtifacts run)
    {
        if (run.Packages.Name != _options.PackagesArtifact
            || (_options.VersionArtifact is not null && run.VersionArtifact?.Name != _options.VersionArtifact))
            throw new InvalidDataException(
                $"Cached run {run.RunId} uses different configured artifacts. Use a different Server.CacheDirectory when changing Artifact or VersionArtifact.")
            { Data = { ["config"] = true } };
    }
    internal static NuGetVersion BuildVersion(BuildEntry build) =>
        string.IsNullOrEmpty(build.Version) ? new NuGetVersion(0, 0, 0) : NuGetVersion.Parse(build.Version);

    private static bool IsBuildFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException
            or System.Xml.XmlException or NuGet.Packaging.Core.PackagingException;

    private void ReportUnavailableBuild(BuildEntry build, Exception exception) =>
        _logger.LogWarning(exception, "Skipping build {RunId} attempt {Attempt} of PR {PullRequest}: " +
            "package artifact {ArtifactId} is unavailable or invalid; existing cache files were retained.",
            build.RunId, build.Attempt, build.PullRequest, build.Packages.Id);

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
        var discovery = await _source.DiscoverAsync(build => _runs.Any(x =>
            x.RunId == build.RunId && x.Attempt == build.Attempt && x.PullRequest == build.PullRequest
            && string.Equals(x.HeadSha, build.HeadSha, StringComparison.OrdinalIgnoreCase)), cancellationToken);
        var openPullRequests = discovery.OpenPullRequests;
        TrackClosedPullRequests(openPullRequests);
        var eligibleRuns = discovery.Builds
            .Where(x => x.State == BuildCompletion.Succeeded)
            .Where(x => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(x.PullRequest)
                || _options.RunIds.Contains(x.RunId))
            .ToArray();
        // A live workflow listing can corroborate cached runs absent from historical run metadata.
        var cachedBuilds = _builds.Select(build => (build.RunId, build.Attempt, build.PullRequest)).ToHashSet();
        var runs = eligibleRuns.GroupBy(x => x.PullRequest)
            .SelectMany(g => g.OrderByDescending(x => x.RunId).Take(1).Concat(g.Where(x =>
                _options.RunIds.Contains(x.RunId)
                || cachedBuilds.Contains((x.RunId, x.Attempt, x.PullRequest)))))
            .DistinctBy(x => (x.RunId, x.Attempt, x.PullRequest)).ToArray();
        // Keep immutable completed-run and artifact metadata on disk even while a PR is closed.
        // Open-PR membership is volatile and is applied only to the response below.
        var next = new List<RunArtifacts>(_runs);
        if (MatchingSnapshot() is { } knownSnapshot)
            foreach (var run in knownSnapshot.Runs)
                if (!next.Any(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest))
                    next.Add(run);
        var skipped = 0;
        string? skippedError = null;
        foreach (var run in runs)
        {
            var pr = run.PullRequest;
            // Completed immutable builds already known locally need no artifact API/download request.
            var existing = next.FindIndex(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == pr);
            // Verified runs are immutable; legacy runs are re-associated with their artifacts from the live listing.
            if (existing >= 0 && next[existing].WorkflowVerified)
            {
                ValidateCachedArtifacts(next[existing]);
                continue;
            }
            IReadOnlyList<Artifact> artifacts;
            try
            {
                artifacts = (await _source.LoadArtifactsAsync(run, cancellationToken)).Artifacts;
            }
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken)
                && !_options.RunIds.Contains(run.RunId))
            {
                skipped++;
                skippedError ??= DiscoveryFailure.Describe(exception);
                _logger.LogWarning("Skipping an unpinned build: {Error}", skippedError);
                continue;
            }
            var packageMatches = artifacts.Where(x => x.Name == _options.PackagesArtifact).Take(2).ToArray();
            var versionMatches = _options.VersionArtifact is null ? [] : artifacts.Where(x => x.Name == _options.VersionArtifact).Take(2).ToArray();
            var packages = packageMatches.Length == 1 ? packageMatches[0] : null;
            var version = versionMatches.Length == 1 ? versionMatches[0] : null;
            var problem = packages is null || (_options.VersionArtifact is not null && version is null)
                ? $"Run {run.RunId} has missing package/version artifacts."
                : ((packages.Expired && !_artifacts.Contains(packages)) || (version is not null && version.Expired && !_artifacts.Contains(version)))
                    ? $"Run {run.RunId} has expired artifacts that are not cached locally."
                    : null;
            if (problem is not null)
            {
                if (_options.RunIds.Contains(run.RunId)) throw new InvalidDataException(problem);
                skipped++;
                skippedError ??= problem;
                _logger.LogWarning("Skipping an unpinned build: {Error}", problem);
                continue;
            }
            next.RemoveAll(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == pr);
            next.Add(new RunArtifacts(run.RunId, run.Attempt, pr, run.HeadSha, packages!, version, WorkflowVerified: true));
        }
        foreach (var id in _options.RunIds)
        {
            if (discovery.Builds.Any(x => x.RunId == id)
                && !next.Any(x => x.RunId == id))
                throw new InvalidDataException($"Run {id} is not an available successful build of an open pull request.");
        }
        var availableCached = next.Where(x => openPullRequests.Contains(x.PullRequest))
            .Where(x => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(x.PullRequest)
                || _options.RunIds.Contains(x.RunId))
            .ToArray();
        foreach (var run in availableCached) ValidateCachedArtifacts(run);
        var selected = availableCached.GroupBy(x => x.PullRequest)
            .Select(g => g.OrderByDescending(x => x.RunId).ThenByDescending(x => x.Attempt).First())
            .Select(x => (x.RunId, x.Attempt, x.PullRequest)).ToHashSet();
        selected.UnionWith(availableCached.Where(x => _options.RunIds.Contains(x.RunId))
            .Select(x => (x.RunId, x.Attempt, x.PullRequest)));
        var result = availableCached.Where(x => selected.Contains((x.RunId, x.Attempt, x.PullRequest)))
            .Select(x => _options.VersionArtifact is null ? x with { VersionArtifact = null } : x)
            .OrderBy(x => x.RunId).ThenBy(x => x.Attempt).ToArray();
        await PersistIncompleteAsync(new Discovery(result, true,
            "Discovery metadata has not yet been committed.", MatchingSnapshot(),
            MetadataIncomplete: true, PersistencePending: true), cancellationToken);
        try
        {
            await JsonSettings.WriteAtomicAsync(_runsFile,
                new RunArtifactsFile(_options.Repository, _options.Workflow, next, SchemaVersion: 2), cancellationToken);
            _runs = next;
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            throw new SnapshotPersistenceException();
        }
        // Earlier runs of still-open PRs are retained for outages only; normal serving uses `result`.
        var retained = availableCached.Select(x => _options.VersionArtifact is null ? x with { VersionArtifact = null } : x)
            .OrderBy(x => x.RunId).ThenBy(x => x.Attempt).ToArray();
        var snapshot = OfflineSnapshot.Create(_options, retained);
        try
        {
            await JsonSettings.WriteAtomicAsync(_snapshotFile, snapshot, cancellationToken);
            if (File.Exists(_incompleteFile)) File.Delete(_incompleteFile);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            throw new SnapshotPersistenceException();
        }
        _snapshot = snapshot;
        _snapshotError = null;
        Volatile.Write(ref _pinnedWorkflowError, null);
        lock (_incompleteGate)
        {
            _incompleteUnreadable = false;
            _incompleteRetryable = false;
            Volatile.Write(ref _partialDiscovery, null);
        }
        lock (_healthGate)
        {
            if (result.Length == 0 && skipped == 0) _contentError = null;
            _health = new CatalogHealth(_contentError is null ? "ok" : "degraded", _options.Repository, _contentError,
                snapshot.LastSuccessfulRefreshUtc,
                "current", snapshot.Runs.Length, null, null, null, null, ProvenanceFor(snapshot.Runs));
        }
        _memoryCache.Set(_cacheKey, result, TimeSpan.FromSeconds(_options.RefreshSeconds));
        return new Discovery(result, false, null, snapshot);
    }

    private async Task PersistIncompleteAsync(Discovery incomplete, CancellationToken cancellationToken)
    {
        lock (_incompleteGate)
        {
            _incompleteGeneration++;
            Volatile.Write(ref _partialDiscovery, new PartialDiscovery(OfflineSnapshot.Create(_options, []), incomplete));
        }
        try
        {
            await JsonSettings.WriteAtomicAsync(_incompleteFile, new IncompleteDiscoveryFile(
                1, _options.Repository, _options.Workflow, _options.PackagesArtifact, _options.VersionArtifact,
                _options.PullRequests.Distinct().Order().ToArray(), _options.RunIds.Distinct().Order().ToArray(),
                ProxyOptions.RunLimit, incomplete.Runs, incomplete.SkippedBuilds, incomplete.MetadataIncomplete,
                incomplete.UnverifiedPinnedRuns), cancellationToken);
            lock (_incompleteGate)
            {
                _incompleteUnreadable = false;
                _incompleteRetryable = false;
            }
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
            throw new SnapshotPersistenceException();
        }
    }

    private static Discovery Remember(FeedRequestScope? scope, Discovery discovery)
    {
        if (scope is not null) scope.Discovery = discovery;
        return discovery;
    }

    private OfflineSnapshot? MatchingSnapshot() =>
        _snapshot is { } snapshot && snapshot.Matches(_options) ? snapshot : null;

    private static string ProvenanceFor(RunArtifacts[]? runs) =>
        runs is not { Length: > 0 } ? "unknown" : runs.All(run => run.WorkflowVerified) ? "verified" : "legacy-unverified";

    private bool IsUnverifiedPinnedRun(RunArtifacts run) =>
        !run.WorkflowVerified && (_options.RunIds.Contains(run.RunId)
            || (_snapshot is { } snapshot && snapshot.MatchesSource(_options)
                && snapshot.RunIds is not null && snapshot.RunIds.Contains(run.RunId)));

    private bool HasUnverifiedPinnedRuns() =>
        (MatchingSnapshot()?.Runs ?? _runs.ToArray()).Any(IsUnverifiedPinnedRun);

    private Discovery? ExcludeUnverifiedPins(Discovery discovery)
    {
        var runs = discovery.Runs.Where(run => !IsUnverifiedPinnedRun(run)).ToArray();
        if (runs.Length == discovery.Runs.Length) return discovery;
        if (runs.Length == 0) return null;
        return discovery with
        {
            Runs = runs,
            Error = discovery.Error + " Legacy pinned runs lack verified workflow provenance.",
            MetadataIncomplete = true,
            UnverifiedPinnedRuns = true
        };
    }

    private Discovery? FindFallback(string error)
    {
        if (_incompleteUnreadable && _incompleteRetryable) LoadIncompleteDiscovery(retry: true);
        if (_incompleteUnreadable) return null;
        var partial = Volatile.Read(ref _partialDiscovery);
        if (partial is not null && partial.Configuration.Matches(_options))
        {
            if (partial.Discovery.Error is { Length: > 0 } previous
                && !error.Contains(previous, StringComparison.Ordinal))
                error += " " + previous;
            return ExcludeUnverifiedPins(partial.Discovery with { IsStale = true, Error = error });
        }
        var snapshot = MatchingSnapshot();
        if (snapshot is not null)
            return snapshot.Runs.Length == 0 ? null : ExcludeUnverifiedPins(new Discovery(snapshot.Runs, true, error, snapshot));
        if ((File.Exists(_snapshotFile) && (_snapshot is null || !_snapshot.MatchesSource(_options)))
            || !File.Exists(_runsFile) || _runsUnreadable) return null;
        var runs = _runs.Where(run => run.Packages.Name == _options.PackagesArtifact
                && run.VersionArtifact?.Name == _options.VersionArtifact
                && (_options.PullRequests.Length == 0 || _options.PullRequests.Contains(run.PullRequest)
                    || _options.RunIds.Contains(run.RunId)))
            .OrderBy(run => run.RunId).ThenBy(run => run.Attempt).ToArray();
        if (runs.Length == 0) return null;
        const string legacy = "Legacy cache open-PR membership is unknown; closed PRs may be included.";
        if (!error.Contains(legacy, StringComparison.Ordinal)) error += " " + legacy;
        var metadataIncomplete = HasUnmatchedCatalogBuilds(runs, null)
            || _options.RunIds.Any(id => runs.All(run => run.RunId != id));
        const string metadata = "Legacy catalog and run metadata do not agree; package inventories may be incomplete.";
        if (metadataIncomplete && !error.Contains(metadata, StringComparison.Ordinal)) error += " " + metadata;
        return ExcludeUnverifiedPins(new Discovery(runs, true, error,
            null, MembershipUnknown: true, MetadataIncomplete: metadataIncomplete));
    }

    private void LoadIncompleteDiscovery(bool retry = false)
    {
        long generation;
        lock (_incompleteGate)
        {
            if (retry && (!_incompleteUnreadable || !_incompleteRetryable || _partialDiscovery is not null)) return;
            generation = _incompleteGeneration;
        }
        try
        {
            var saved = JsonSerializer.Deserialize<IncompleteDiscoveryFile>(
                File.ReadAllText(_incompleteFile), JsonSettings.Options);
            if (saved is null || saved.SchemaVersion != 1
                || saved.Repository != _options.Repository || saved.Workflow != _options.Workflow
                || saved.PackagesArtifact != _options.PackagesArtifact || saved.VersionArtifact != _options.VersionArtifact
                || saved.PullRequests is null || saved.RunIds is null || saved.RunLimit <= 0 || saved.Runs is null
                || saved.SkippedBuilds < 0 || !(saved.SkippedBuilds > 0 || saved.MetadataIncomplete || saved.UnverifiedPinnedRuns)
                || !OfflineSnapshot.Create(_options, saved.Runs).IsValid())
                throw new JsonException("Invalid incomplete discovery metadata.");
            var matchingSelection = saved.PullRequests.SequenceEqual(_options.PullRequests.Distinct().Order())
                && saved.RunIds.SequenceEqual(_options.RunIds.Distinct().Order()) && saved.RunLimit == ProxyOptions.RunLimit;
            var runs = saved.Runs.Where(run =>
                    _options.PullRequests.Length == 0 || _options.PullRequests.Contains(run.PullRequest)
                    || _options.RunIds.Contains(run.RunId))
                .ToArray();
            var error = saved.UnverifiedPinnedRuns
                ? "A pinned run could not be verified against the configured workflow. A complete live refresh is required."
                : "A previous discovery was incomplete; a complete live refresh is required.";
            var discovery = new Discovery(runs, true, error, MatchingSnapshot(),
                MembershipUnknown: !matchingSelection, SkippedBuilds: saved.SkippedBuilds,
                MetadataIncomplete: saved.MetadataIncomplete || !matchingSelection,
                UnverifiedPinnedRuns: saved.UnverifiedPinnedRuns);
            lock (_incompleteGate)
            {
                // A live refresh may have produced newer state while this file was being read.
                if (_incompleteGeneration != generation
                    || (retry && (!_incompleteUnreadable || !_incompleteRetryable || _partialDiscovery is not null))) return;
                if (saved.UnverifiedPinnedRuns) Volatile.Write(ref _pinnedWorkflowError, error);
                Volatile.Write(ref _partialDiscovery, new PartialDiscovery(OfflineSnapshot.Create(_options, []), discovery));
                _incompleteUnreadable = false;
                _incompleteRetryable = false;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // A previously unreadable marker cannot become trusted merely because it disappeared.
        }
        catch (JsonException)
        {
            MarkIncompleteUnreadable(generation, retryable: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MarkIncompleteUnreadable(generation, retryable: true);
        }
    }

    private void MarkIncompleteUnreadable(long generation, bool retryable)
    {
        var log = false;
        lock (_incompleteGate)
        {
            if (_incompleteGeneration != generation || _partialDiscovery is not null) return;
            log = !_incompleteUnreadable || _incompleteRetryable != retryable;
            _incompleteRetryable = retryable;
            _incompleteUnreadable = true;
        }
        if (log)
            _logger.LogWarning("Incomplete discovery metadata is {Reason}.",
                retryable ? "temporarily inaccessible" : "invalid");
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
                && run.VersionArtifact?.Id == build.VersionArtifact?.Id));

    private bool HasContentFailure()
    {
        lock (_healthGate) return _contentError is not null;
    }

    private void MarkContentHealthy(Discovery discovery, bool pendingOnly = false)
    {
        if (discovery.IsPartial || discovery.Snapshot is null) return;
        lock (_healthGate)
        {
            if (_contentError is null || (pendingOnly && _contentError != ContentPending)
                || _health.Membership != "current"
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
            scope.Availability = new FeedAvailability(false, false, "current", discovery.Provenance);
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
                discovery is { SkippedBuilds: > 0 } ? discovery.SkippedBuilds : null, null, null,
                discovery?.Provenance ?? ProvenanceFor(snapshot?.Runs));
        }
    }

    private async Task<OfflineResolution> RequireOfflinePackagesAsync(Discovery discovery, CancellationToken cancellationToken)
    {
        var resolution = await ResolveOfflineAsync(discovery, cancellationToken);
        if (resolution.Packages.Length == 0)
            throw new DiscoveryUnavailableException(discovery.Error
                + (resolution.VerificationPending ? " Cached content verification is still in progress." : "")
                + " No usable cached packages are available for this snapshot.");
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
        if (scope is not null) scope.Availability = new FeedAvailability(true, resolution.IsPartial, discovery.Membership, discovery.Provenance);
        return resolution;
    }

    private async Task<OfflineResolution> ResolveOfflineAsync(Discovery discovery, CancellationToken cancellationToken)
    {
        var scope = _requestScope.Value;
        if (scope?.Offline is { } cached) return cached;

        var knownBuilds = Volatile.Read(ref _builds);
        {
            var packages = new List<PublishedPackage>();
            var builds = new List<BuildEntry>();
            var availableBuilds = 0;
            var unavailableBuilds = discovery.SkippedBuilds;
            var unavailablePackages = 0;
            var unavailableMetadata = 0;
            var unknownInventory = discovery.MetadataIncomplete || discovery.SkippedBuilds > 0;
            var verificationPending = false;
            var missingPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolved = await Task.WhenAll(discovery.Runs.Select(run =>
                ResolveOfflineRunAsync(run, knownBuilds, cancellationToken)));
            for (var index = 0; index < discovery.Runs.Length; index++)
            {
                var run = discovery.Runs[index];
                var (build, available, pending) = resolved[index];
                verificationPending |= pending;
                var template = build ?? new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, "", "",
                    run.Packages, run.VersionArtifact);
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
                unknownInventory, missingPackageIds, discovery.IsPartial || discovery.MembershipUnknown, verificationPending);
            lock (_healthGate)
            {
                if (_health.Status == "degraded" && !verificationPending && !discovery.PersistencePending
                    && (result.UnavailableBuilds > discovery.SkippedBuilds
                    || result.UnavailablePackages > 0 || result.UnavailableBuildMetadata > 0))
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
    }

    private async Task<OfflineRun> ResolveOfflineRunAsync(
        RunArtifacts run, List<BuildEntry> knownBuilds, CancellationToken cancellationToken)
    {
        var pending = false;
        var build = knownBuilds.FirstOrDefault(x => x.RunId == run.RunId && x.Attempt == run.Attempt
            && x.PullRequest == run.PullRequest && x.Packages is not null && x.Packages.Id == run.Packages.Id
            && x.VersionArtifact?.Id == run.VersionArtifact?.Id);
        if (build is null && run.VersionArtifact is not null && _artifacts.Contains(run.VersionArtifact))
        {
            try
            {
                var work = StartOfflineVersionRead(run.VersionArtifact);
                if (await CompletesWithinRequestAsync(work, cancellationToken, OfflineWait))
                {
                    var original = await work;
                    build = new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, original,
                        PackageRewriter.VersionFor(original, run.PullRequest, run.RunId, run.Attempt, run.Packages.Id),
                        run.Packages, run.VersionArtifact);
                }
                else pending = true;
            }
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
            {
            }
        }

        var template = build ?? new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, "", "",
            run.Packages, run.VersionArtifact);
        try
        {
            var work = TrackBackground(() => Task.Run(() => _packages.GetAvailableOfflineAsync(template, _stopping)));
            if (await CompletesWithinRequestAsync(work, cancellationToken, OfflineWait))
                return new OfflineRun(build, await work, pending);
            pending = true;

            var inspect = TrackBackground(() => Task.Run(() => _packages.GetAvailableOfflineAsync(template, _stopping, allowRepair: false)));
            if (await CompletesWithinRequestAsync(inspect, cancellationToken, OfflineWait))
                return new OfflineRun(build, await inspect, pending);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
        {
        }
        return new OfflineRun(build, new CachedPackageAvailability([], [], false), pending);
    }

    private Task<string> StartOfflineVersionRead(Artifact artifact)
    {
        lock (_offlineVersionGate)
        {
            if (_offlineVersionReads.TryGetValue(artifact.Id, out var pending) && !pending.IsCompleted)
                return pending;
            var work = TrackBackground(() => Task.Run(() => ReadVersionAsync(artifact, _stopping)));
            return _offlineVersionReads[artifact.Id] = work;
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
                || (build.VersionArtifact is not null
                    && (!NuGetVersion.TryParse(build.OriginalVersion, out _) || !NuGetVersion.TryParse(build.Version, out _)))
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
        if (saved.SchemaVersion > CacheSchema.Version)
            throw new InvalidDataException("This cache was created by a newer version. Use a different CacheDirectory or upgrade 127Cache.");
        _builds = saved.Builds.Select(build => build.VersionArtifact is null ? build : build with
        {
            Version = PackageRewriter.VersionFor(build.OriginalVersion, build.PullRequest, build.RunId, build.Attempt, build.Packages.Id)
        }).ToList();
        _catalogSchema = saved.SchemaVersion;
        _catalogNeedsUpgrade = saved.SchemaVersion != CacheSchema.Version || !saved.Builds.SequenceEqual(_builds);
        _catalogUnreadable = false;
    }

    private void LoadRuns()
    {
        if (!File.Exists(_runsFile))
        {
            // catalog.json identifies the repository, but not the workflow, so these runs stay unverified.
            _runs = _builds
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
            if (saved.SchemaVersion is not (1 or 2) || saved.Runs is null || saved.Runs.Any(run => run is null || run.RunId <= 0 || run.Attempt <= 0
                || run.PullRequest <= 0 || run.HeadSha is null || run.Packages is null || run.Packages.Id <= 0
                || run.VersionArtifact is { Id: <= 0 })
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
        _runs = saved.SchemaVersion == 1
            ? saved.Runs.Select(run => run with { WorkflowVerified = false }).ToList()
            : saved.Runs;
        _runsUnreadable = false;
    }

    private async Task<string> ReadVersionAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var path = await _packages.GetVersionArtifactAsync(artifact, cancellationToken);
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(x => x.FullName == "version.txt").ToArray();
        if (entries.Length != 1) throw new InvalidDataException($"Artifact {artifact.Id} must contain exactly one version.txt.");
        var entry = entries[0];
        if (entry.Length > 4096) throw new InvalidDataException("Version file is unexpectedly large.");
        using var reader = new StreamReader(entry.Open());
        var value = (await reader.ReadToEndAsync(cancellationToken)).Trim();
        return NuGetVersion.TryParse(value, out var version) ? version.ToNormalizedString()
            : throw new InvalidDataException($"Artifact {artifact.Id} contains an invalid NuGet version.");
    }
}