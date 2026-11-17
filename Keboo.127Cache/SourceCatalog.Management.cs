using NuGet.Versioning;

namespace Keboo.LocalCache;

internal sealed record SourceRefreshOutcome(bool Succeeded, int Builds, string? Error, DateTimeOffset? RefreshedUtc);

internal sealed partial class SourceCatalog
{
    private readonly object _forcedRefreshGate = new();
    private Task<SourceRefreshOutcome>? _forcedRefresh;

    // Builds recorded in the on-disk catalog; reading them never contacts the upstream pipeline.
    internal BuildEntry[] GetCachedBuilds() => Volatile.Read(ref _builds).ToArray();

    internal string? GetPullRequestTitle(int pullRequest) =>
        Volatile.Read(ref _runs).Concat(MatchingSnapshot()?.Runs ?? [])
            .FirstOrDefault(x => x.PullRequest == pullRequest && !string.IsNullOrWhiteSpace(x.PullRequestTitle))?.PullRequestTitle;

    // The overview already runs off-request, so it must follow discovery/preparation to completion instead of
    // using the feed's bounded HTTP wait and mistaking a still-running operation for its final result.
    internal Task<RunArtifacts[]> GetOverviewRunsAsync(CancellationToken token) =>
        Task.FromResult(PublishedPackages().Select(x => x.Build)
            .DistinctBy(x => (x.PullRequest, x.RunId, x.Attempt))
            .Select(x => new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact))
            .ToArray());

    internal async Task PrepareOverviewPackagesAsync(CancellationToken token)
    {
        await StartForcedRefresh().Work.WaitAsync(token);
    }

    // Starts a rediscovery that ignores the memory cache. Callers that arrive while one is running share it.
    internal (Task<SourceRefreshOutcome> Work, bool Coalesced) StartForcedRefresh()
    {
        lock (_forcedRefreshGate)
        {
            if (_forcedRefresh is { IsCompleted: false } running) return (running, true);
            return (_forcedRefresh = TrackBackground(ForceRefreshAsync), false);
        }
    }

    private async Task<SourceRefreshOutcome> ForceRefreshAsync()
    {
        if (!_options.HasSource) return new SourceRefreshOutcome(true, 0, null, null);
        await _publicationGate.WaitAsync(_stopping);
        try
        {
            await RestorePublishedAsync(_stopping);
            _memoryCache.Remove(_cacheKey);
            var discovery = await StartRefresh();
            if (discovery.IsStale)
            {
                var offline = await ResolveOfflineAsync(discovery, _stopping);
                var existing = PublishedPackages();
                var combined = existing.Concat(offline.Packages)
                    .DistinctBy(package => (package.Id.ToLowerInvariant(), package.Build.Version))
                    .OrderBy(package => NuGetVersion.Parse(package.Build.Version), VersionComparer.VersionRelease)
                    .ToArray();
                if (combined.Length > existing.Length)
                    await PublishPackagesAsync(combined, _stopping);
                return new SourceRefreshOutcome(false, discovery.Runs.Length, discovery.Error, GetHealth().LastSuccessfulRefreshUtc);
            }
            Task<OnlinePackages>? work;
            while ((work = StartContentRefresh(discovery)) is null)
            {
                Task pending;
                lock (_contentRefreshGate) pending = _contentRefresh!.Work;
                await pending.WaitAsync(_stopping);
            }
            var result = await work;
            if (result.Failure is not null)
            {
                await RecoverFromContentFailureAsync(result.Failure, discovery, _stopping);
                if (PublishedPackages().Length == 0 && result.Packages.Length > 0)
                    await PublishPackagesAsync(result.Packages, _stopping);
                return new SourceRefreshOutcome(false, discovery.Runs.Length, DiscoveryFailure.DescribeContent(result.Failure),
                    GetHealth().LastSuccessfulRefreshUtc);
            }
            await PublishPackagesAsync(result.Packages, _stopping);
            MarkContentHealthy(discovery);
            return new SourceRefreshOutcome(true, discovery.Runs.Length, null, GetHealth().LastSuccessfulRefreshUtc);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, _stopping))
        {
            if (Volatile.Read(ref _pinnedWorkflowError) is not null)
                Publish(Volatile.Read(ref _published).Items
                    .Where(item => !_options.RunIds.Contains(item.Package.Build.RunId)).ToArray());
            var error = exception is DiscoveryUnavailableException ? exception.Message : DiscoveryFailure.Describe(exception);
            SetFailureHealth(error, FindFallback(error), contentFailure: true);
            _logger.LogWarning("Forced refresh of source '{Source}' failed: {Error}", Name, error);
            return new SourceRefreshOutcome(false, 0, error, GetHealth().LastSuccessfulRefreshUtc);
        }
        finally
        {
            _publicationGate.Release();
        }
    }
}