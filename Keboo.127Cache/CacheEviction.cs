using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache;

// Why a removed build is, or is not, expected to be cached again by the next refresh.
public enum ReappearanceOutcome
{
    // Upstream could not be queried (for example offline), so nothing is known.
    Unknown,
    // The pull request is open, this is its latest successful run and the artifacts are still available upstream.
    WillReappear,
    PullRequestClosed,
    // The artifacts expired upstream, so the removed copy can never be downloaded again.
    ArtifactExpired,
    // A newer run of the same pull request is served instead.
    SupersededByNewerRun,
    // The run or its artifacts are no longer listed upstream, or the source configuration excludes the pull request.
    NotAvailableUpstream
}

public sealed record RemovedBuild(
    string Source, int PullRequest, long RunId, int Attempt, long BytesReclaimed,
    ReappearanceOutcome Reappearance, string? Error = null)
{
    public bool Removed => Error is null;
    public bool WillReappear => Removed && Reappearance == ReappearanceOutcome.WillReappear;
    // True when the removal destroyed the only copy: the artifacts expired upstream and cannot be downloaded again.
    public bool IsPermanent => Removed && Reappearance == ReappearanceOutcome.ArtifactExpired;
}

public sealed record RemovalResult(IReadOnlyList<RemovedBuild> Builds, long BytesReclaimed, bool DryRun = false)
{
    public static RemovalResult Empty { get; } = new([], 0);
    public int RemovedCount => Builds.Count(x => x.Removed);
    public bool AnyWillReappear => Builds.Any(x => x.WillReappear);
    public bool AnyPermanent => Builds.Any(x => x.IsPermanent);
}

public sealed record BuildDiskUsage(
    string Source, int PullRequest, long RunId, int Attempt, long ArtifactBytes, long PackageBytes,
    DateTimeOffset? PullRequestClosedSinceUtc, bool IsPartial, DateTimeOffset? CachedUtc = null)
{
    public long TotalBytes => ArtifactBytes + PackageBytes;
}

public sealed record SourceDiskUsage(string Source, long TotalBytes, long OtherBytes, IReadOnlyList<BuildDiskUsage> Builds)
{
    public long BuildBytes => Builds.Sum(x => x.TotalBytes);
}

public sealed record CacheDiskUsage(long TotalBytes, IReadOnlyList<SourceDiskUsage> Sources);

// Removes cached builds. Removal deletes cached data only: nothing records that a build was removed, so the next
// refresh downloads it again when its pull request is still open and its artifacts are still available upstream.
public interface ICacheEvictionService
{
    Task<CacheDiskUsage> GetDiskUsageAsync(CancellationToken cancellationToken = default);

    // Throws KeyNotFoundException for an unknown source.
    Task<SourceDiskUsage> GetSourceDiskUsageAsync(string source, CancellationToken cancellationToken = default);

    // Removing a build that is not cached is not an error: the result simply lists no builds.
    // With dryRun nothing is deleted; the result reports what would be reclaimed and whether it would reappear.
    Task<RemovalResult> RemoveBuildAsync(string source, long runId, int attempt, bool dryRun = false,
        CancellationToken cancellationToken = default);

    // Removes every cached run of one pull request (open or closed).
    Task<RemovalResult> RemovePullRequestAsync(string source, int pullRequest, bool dryRun = false,
        CancellationToken cancellationToken = default);

    // Empties the cache of one source, including files no longer referenced by any metadata.
    Task<RemovalResult> RemoveSourceAsync(string source, bool dryRun = false, CancellationToken cancellationToken = default);

    // Removes builds of pull requests that upstream reports as closed. closedFor limits this to pull requests that
    // this cache has observed as closed for at least that long. Nothing is removed when upstream cannot be queried.
    Task<RemovalResult> RemoveClosedPullRequestsAsync(string? source = null, TimeSpan? closedFor = null, bool dryRun = false,
        CancellationToken cancellationToken = default);

    // Applies Server.EvictClosedPullRequestsAfterDays; does nothing while that setting is off.
    Task<RemovalResult> ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default);
}

public sealed class CacheEvictionService(PackageCatalog catalog, IOptions<ProxyOptions> proxyOptions,
    ILogger<CacheEvictionService>? logger = null)
    : ICacheEvictionService
{
    private readonly ProxyOptions _options = proxyOptions.Value;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<CacheDiskUsage> GetDiskUsageAsync(CancellationToken cancellationToken = default)
    {
        var sources = new List<SourceDiskUsage>();
        foreach (var source in catalog.Sources)
            sources.Add(await source.GetDiskUsageAsync(cancellationToken));
        return new CacheDiskUsage(sources.Sum(x => x.TotalBytes), sources);
    }

    public Task<SourceDiskUsage> GetSourceDiskUsageAsync(string source, CancellationToken cancellationToken = default) =>
        Find(source).GetDiskUsageAsync(cancellationToken);

    public Task<RemovalResult> RemoveBuildAsync(string source, long runId, int attempt, bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RemoveAsync(Find(source), new RemovalScope(key => key.RunId == runId && key.Attempt == attempt), dryRun, cancellationToken);

    public Task<RemovalResult> RemovePullRequestAsync(string source, int pullRequest, bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        RemoveAsync(Find(source), new RemovalScope(key => key.PullRequest == pullRequest), dryRun, cancellationToken);

    public Task<RemovalResult> RemoveSourceAsync(string source, bool dryRun = false, CancellationToken cancellationToken = default) =>
        RemoveAsync(Find(source), new RemovalScope(_ => true, SweepSource: true), dryRun, cancellationToken);

    public async Task<RemovalResult> RemoveClosedPullRequestsAsync(string? source = null, TimeSpan? closedFor = null,
        bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (closedFor is { } age && age < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(closedFor));
        var sources = source is null ? catalog.Sources : [Find(source)];
        var scope = new RemovalScope(_ => true, OnlyClosedPullRequests: true, ClosedFor: closedFor);
        var results = new List<RemovalResult>();
        foreach (var target in sources)
            results.Add(await RemoveAsync(target, scope, dryRun, cancellationToken));
        return Combine(results, dryRun);
    }

    public async Task<RemovalResult> ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (_options.EvictClosedPullRequestsAfterDays is not > 0) return RemovalResult.Empty;
        var result = await RemoveClosedPullRequestsAsync(
            closedFor: TimeSpan.FromDays(_options.EvictClosedPullRequestsAfterDays.Value), cancellationToken: cancellationToken);
        if (result.RemovedCount > 0)
            _logger.LogInformation("Removed {Count} cached builds of pull requests closed for more than {Days} days, reclaiming {Bytes} bytes.",
                result.RemovedCount, _options.EvictClosedPullRequestsAfterDays, result.BytesReclaimed);
        return result;
    }

    private SourceCatalog Find(string source) =>
        catalog.Sources.FirstOrDefault(x => string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Source '{source}' is not configured or could not be started.");

    private async Task<RemovalResult> RemoveAsync(SourceCatalog source, RemovalScope scope, bool dryRun, CancellationToken cancellationToken)
    {
        var removal = await source.RemoveAsync(scope, dryRun, cancellationToken);
        var builds = removal.Builds.Select(x => new RemovedBuild(source.Name, x.Key.PullRequest, x.Key.RunId, x.Key.Attempt,
            x.BytesReclaimed, x.Reappearance, x.Error)).ToArray();
        foreach (var failed in builds.Where(x => !x.Removed))
            _logger.LogWarning("Could not remove build {RunId} attempt {Attempt} of source '{Source}': {Error}",
                failed.RunId, failed.Attempt, failed.Source, failed.Error);
        return new RemovalResult(builds, builds.Sum(x => x.BytesReclaimed) + removal.UnattributedBytes, dryRun);
    }

    private static RemovalResult Combine(IReadOnlyList<RemovalResult> results, bool dryRun) =>
        new(results.SelectMany(x => x.Builds).ToArray(), results.Sum(x => x.BytesReclaimed), dryRun);
}

// Runs the optional retention policy shortly after startup and then hourly.
internal sealed class CacheRetentionService(ICacheEvictionService eviction, IOptions<ProxyOptions> proxyOptions,
    ILogger<CacheRetentionService> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (proxyOptions.Value.EvictClosedPullRequestsAfterDays is not > 0) return;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await eviction.ApplyRetentionPolicyAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Closed pull request retention policy failed; it will run again later.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

internal sealed record RemovalScope(
    Func<BuildKey, bool> Match, bool SweepSource = false, bool OnlyClosedPullRequests = false, TimeSpan? ClosedFor = null);

internal sealed record BuildRemoval(BuildKey Key, long BytesReclaimed, ReappearanceOutcome Reappearance, string? Error);

internal sealed record SourceRemoval(IReadOnlyList<BuildRemoval> Builds, long UnattributedBytes)
{
    public static SourceRemoval Empty { get; } = new([], 0);
}
