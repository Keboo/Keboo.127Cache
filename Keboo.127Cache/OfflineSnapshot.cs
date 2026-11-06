using System.Text.Json;

namespace Keboo._127Cache;

internal sealed record OfflineSnapshot(
    int SchemaVersion,
    string Repository,
    string Workflow,
    string PackagesArtifact,
    string VersionArtifact,
    int[] PullRequests,
    long[] RunIds,
    int RunLimit,
    DateTimeOffset LastSuccessfulRefreshUtc,
    RunArtifacts[] Runs)
{
    private const int CurrentSchemaVersion = 1;

    public static OfflineSnapshot Create(ProxyOptions options, RunArtifacts[] runs) => new(
        CurrentSchemaVersion, options.Repository, options.Workflow, options.PackagesArtifact, options.VersionArtifact,
        options.PullRequests.Distinct().Order().ToArray(), options.RunIds.Distinct().Order().ToArray(),
        options.RunLimit, DateTimeOffset.UtcNow, runs);

    public bool Matches(ProxyOptions options) =>
        SchemaVersion == CurrentSchemaVersion
        && Repository == options.Repository && Workflow == options.Workflow
        && PackagesArtifact == options.PackagesArtifact && VersionArtifact == options.VersionArtifact
        && PullRequests is not null && PullRequests.SequenceEqual(options.PullRequests.Distinct().Order())
        && RunIds is not null && RunIds.SequenceEqual(options.RunIds.Distinct().Order())
        && RunLimit == options.RunLimit;

    public bool IsValid() =>
        LastSuccessfulRefreshUtc != default && Runs is not null
        && Runs.All(run => run is not null && run.RunId > 0 && run.Attempt > 0 && run.PullRequest > 0
            && run.HeadSha is not null && run.Packages is not null && run.Packages.Id > 0 && run.Packages.Name == PackagesArtifact
            && run.VersionArtifact is not null && run.VersionArtifact.Id > 0 && run.VersionArtifact.Name == VersionArtifact)
        && Runs.Select(run => (run.RunId, run.Attempt, run.PullRequest)).Distinct().Count() == Runs.Length;

    public static OfflineSnapshot? Load(string path, ProxyOptions options, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = "No saved successful discovery snapshot is available.";
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<OfflineSnapshot>(File.ReadAllText(path), JsonSettings.Options);
            if (snapshot is null || !snapshot.IsValid())
            {
                error = "The saved discovery snapshot is incomplete or invalid.";
                return null;
            }
            if (!snapshot.Matches(options))
            {
                error = "The saved discovery snapshot belongs to a different source or discovery configuration.";
                return null;
            }
            return snapshot;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            error = "The saved discovery snapshot cannot be read.";
            return null;
        }
    }
}

public sealed record CatalogHealth(
    string Status,
    string Repository,
    string? Error,
    DateTimeOffset? LastSuccessfulRefreshUtc,
    string Membership,
    int SnapshotBuilds,
    int? AvailableCachedBuilds,
    int? UnavailableBuilds,
    int? UnavailablePackages,
    int? UnavailableBuildMetadata);