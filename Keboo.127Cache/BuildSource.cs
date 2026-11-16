using Microsoft.Extensions.Options;

namespace Keboo.LocalCache;

public enum BuildCompletion { InProgress, Succeeded, Failed }

public sealed record Artifact(long Id, string Name, bool Expired, string? Digest);

// Provider-neutral pull-request build. Artifacts are empty until LoadArtifactsAsync populates them.
public sealed record SourceBuild(
    string SourceName, int PullRequest, long RunId, int Attempt, string HeadSha, BuildCompletion State,
    IReadOnlyList<Artifact> Artifacts);

public sealed record BuildDiscovery(IReadOnlySet<int> OpenPullRequests, IReadOnlyList<SourceBuild> Builds);

public interface IBuildSource
{
    string Name { get; }

    // Returns builds of open pull requests. isVerified reports builds whose association is already trusted locally.
    Task<BuildDiscovery> DiscoverAsync(Func<SourceBuild, bool> isVerified, CancellationToken cancellationToken);

    Task<SourceBuild> LoadArtifactsAsync(SourceBuild build, CancellationToken cancellationToken);

    Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken);
}

public interface IBuildSourceFactory
{
    IBuildSource Create(ProxyOptions source);
}

internal sealed class DefaultBuildSourceFactory(IServiceProvider services) : IBuildSourceFactory
{
    // A registered IGitHubClient replaces the gh-based client (used by tests and embedding hosts).
    public IBuildSource Create(ProxyOptions source) => new GitHubActionsBuildSource(
        source, services.GetService<IGitHubClient>() ?? new GitHubClient(Options.Create(source)),
        services.GetService<ILogger<GitHubActionsBuildSource>>());
}
