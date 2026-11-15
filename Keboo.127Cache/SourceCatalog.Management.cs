namespace Keboo.LocalCache;

internal sealed record SourceRefreshOutcome(bool Succeeded, int Builds, string? Error, DateTimeOffset? RefreshedUtc);

internal sealed partial class SourceCatalog
{
    private readonly object _forcedRefreshGate = new();
    private Task<SourceRefreshOutcome>? _forcedRefresh;

    // Builds recorded in the on-disk catalog; reading them never contacts the upstream pipeline.
    internal BuildEntry[] GetCachedBuilds() => Volatile.Read(ref _builds).ToArray();

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
        try
        {
            _memoryCache.Remove(_cacheKey);
            var discovery = await StartRefresh();
            return new SourceRefreshOutcome(!discovery.IsStale, discovery.Runs.Length, discovery.Error,
                GetHealth().LastSuccessfulRefreshUtc);
        }
        catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, _stopping))
        {
            var error = exception is DiscoveryUnavailableException ? exception.Message : DiscoveryFailure.Describe(exception);
            _logger.LogWarning("Forced refresh of source '{Source}' failed: {Error}", Name, error);
            return new SourceRefreshOutcome(false, 0, error, GetHealth().LastSuccessfulRefreshUtc);
        }
    }
}
