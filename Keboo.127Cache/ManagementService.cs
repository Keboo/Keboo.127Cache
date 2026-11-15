using NuGet.Versioning;

namespace Keboo.LocalCache;

public sealed record SourceInfo(
    string Name, string Repository, string Workflow, string Artifact, string? VersionArtifact, string Status,
    DateTimeOffset? LastRefreshUtc, string? LastError, string Membership);

// CachedState is "cached" (packages complete on disk), "partial" (some files present) or "notCached".
// Served is true when the build is part of the current feed; Version is only known once the build was materialized.
public sealed record BuildInfo(
    string Source, int PullRequest, long RunId, int Attempt, string? HeadSha, string? Version, string? OriginalVersion,
    long SizeBytes, string CachedState, bool Served, DateTimeOffset? PullRequestClosedSinceUtc,
    DateTimeOffset? CachedUtc = null);

public sealed record PackageVersionInfo(string Version, string OriginalVersion, string Source, int PullRequest, long RunId, int Attempt);

public sealed record PackageInfo(string Id, IReadOnlyList<PackageVersionInfo> Versions);

public sealed record SourceRefreshResult(
    string Source, bool Succeeded, int Builds, string? Error, DateTimeOffset? RefreshedUtc, bool Coalesced);

public sealed record RefreshSummary(bool Succeeded, IReadOnlyList<SourceRefreshResult> Sources);

// Read and refresh operations shared by the JSON API and the web UI. Removals are served by ICacheEvictionService.
public interface IManagementService
{
    Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken cancellationToken = default);

    // Throws KeyNotFoundException for an unknown source. Never downloads build content.
    Task<IReadOnlyList<BuildInfo>> GetBuildsAsync(string? source = null, CancellationToken cancellationToken = default);

    // Same content as the NuGet feed, so the first call may materialize builds. Throws DiscoveryUnavailableException
    // when the feed cannot currently be enumerated.
    Task<IReadOnlyList<PackageInfo>> GetPackagesAsync(string? source = null, CancellationToken cancellationToken = default);

    // Rediscovers one source (or all) ignoring the memory cache. Concurrent calls for a source share one discovery.
    Task<RefreshSummary> RefreshAsync(string? source = null, CancellationToken cancellationToken = default);
}

public sealed class ManagementService(PackageCatalog catalog, ICacheEvictionService eviction) : IManagementService
{
    public Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SourceInfo>();
        foreach (var source in catalog.Sources)
        {
            var health = source.GetHealth();
            var options = source.Options;
            result.Add(new SourceInfo(source.Name, options.Repository, options.Workflow, options.PackagesArtifact,
                options.VersionArtifact, health.Status, health.LastSuccessfulRefreshUtc, health.Error, health.Membership));
        }
        foreach (var (name, repository, error) in catalog.FailedSources)
            result.Add(new SourceInfo(name, repository, "", "", null, "failed", null, error, "unknown"));
        return Task.FromResult<IReadOnlyList<SourceInfo>>(result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<IReadOnlyList<BuildInfo>> GetBuildsAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        var result = new List<BuildInfo>();
        foreach (var target in Select(source))
        {
            var usage = await eviction.GetSourceDiskUsageAsync(target.Name, cancellationToken);
            var cached = target.GetCachedBuilds().ToDictionary(x => (x.PullRequest, x.RunId, x.Attempt));
            var served = new HashSet<(int, long, int)>();
            try
            {
                foreach (var run in await target.GetRunArtifactsAsync(cancellationToken))
                    served.Add((run.PullRequest, run.RunId, run.Attempt));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException
                && !cancellationToken.IsCancellationRequested)
            {
                // Without discovery nothing is known to be served; cached builds are still listed.
            }

            var seen = new HashSet<(int, long, int)>();
            foreach (var disk in usage.Builds)
            {
                var key = (disk.PullRequest, disk.RunId, disk.Attempt);
                seen.Add(key);
                cached.TryGetValue(key, out var entry);
                var state = disk.PackageBytes > 0 && !disk.IsPartial ? "cached" : "partial";
                var version = Nullable(entry?.Version);
                var original = Nullable(entry?.OriginalVersion);
                if (entry is not null && version is null && state == "cached")
                {
                    // Without a version artifact the version comes from the packages, which are read from the cache.
                    try
                    {
                        var versions = (await target.Packages.GetPackagesAsync(entry, cancellationToken))
                            .Select(x => (x.Version, x.OriginalVersion)).Distinct().ToArray();
                        if (versions.Length == 1) (version, original) = versions[0];
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException
                        && !cancellationToken.IsCancellationRequested)
                    {
                    }
                }
                result.Add(new BuildInfo(target.Name, disk.PullRequest, disk.RunId, disk.Attempt, entry?.HeadSha,
                    version, original, disk.TotalBytes, state, served.Contains(key), disk.PullRequestClosedSinceUtc, disk.CachedUtc));
            }
            foreach (var key in served.Where(x => !seen.Contains(x)))
                result.Add(new BuildInfo(target.Name, key.Item1, key.Item2, key.Item3, null, null, null, 0, "notCached", true, null));
        }
        return result.OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.PullRequest)
            .ThenBy(x => x.RunId).ThenBy(x => x.Attempt).ToArray();
    }

    public async Task<IReadOnlyList<PackageInfo>> GetPackagesAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        Select(source);
        var packages = await catalog.GetPackagesAsync(cancellationToken);
        return packages
            .Where(x => source is null || string.Equals(x.Build.SourceName, source, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackageInfo(group.First().Id, group
                .OrderBy(x => NuGetVersion.Parse(x.Build.Version), VersionComparer.VersionRelease)
                .Select(x => new PackageVersionInfo(x.Build.Version, x.Build.OriginalVersion, x.Build.SourceName,
                    x.Build.PullRequest, x.Build.RunId, x.Build.Attempt)).ToArray()))
            .ToArray();
    }

    public async Task<RefreshSummary> RefreshAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        var started = Select(source).Select(x => (x.Name, Refresh: x.StartForcedRefresh())).ToArray();
        var results = new List<SourceRefreshResult>();
        foreach (var (name, refresh) in started)
        {
            var outcome = await refresh.Work.WaitAsync(cancellationToken);
            results.Add(new SourceRefreshResult(name, outcome.Succeeded, outcome.Builds, outcome.Error, outcome.RefreshedUtc,
                refresh.Coalesced));
        }
        foreach (var (name, _, error) in catalog.FailedSources.Where(x => source is null
            || string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase)))
            results.Add(new SourceRefreshResult(name, false, 0, error, null, false));
        return new RefreshSummary(results.All(x => x.Succeeded), results);
    }

    private IReadOnlyList<SourceCatalog> Select(string? source)
    {
        if (source is null) return catalog.Sources;
        var match = catalog.Sources.Where(x => string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (match.Length == 0 && !catalog.FailedSources.Any(x => string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase)))
            throw new KeyNotFoundException($"Source '{source}' is not configured or could not be started.");
        return match;
    }

    private static string? Nullable(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
