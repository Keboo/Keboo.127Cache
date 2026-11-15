using Microsoft.Extensions.Logging.Abstractions;

namespace Keboo.LocalCache;

public sealed class GitHubActionsBuildSource(ProxyOptions options, IGitHubClient github, ILogger? logger = null) : IBuildSource
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Name => options.Name;

    public async Task<BuildDiscovery> DiscoverAsync(Func<SourceBuild, bool> isVerified, CancellationToken cancellationToken)
    {
        var pullRequests = await github.GetOpenPullRequestsAsync(cancellationToken);
        var openPullRequests = pullRequests.Select(x => x.Number).ToHashSet();
        var openByHeadSha = pullRequests
            .Where(x => !string.IsNullOrWhiteSpace(x.Head?.Sha))
            .GroupBy(x => x.Head!.Sha!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var discovered = openPullRequests.Count == 0 ? [] : await github.GetRunsAsync(cancellationToken);
        var builds = new List<SourceBuild>();
        var commitHistory = new Dictionary<int, HashSet<string>>();
        var pullRequestHistory = new Dictionary<(string Repository, string Branch), int[]>();
        foreach (var run in discovered.Where(run => IsSuccessfulPullRequestRun(run) || options.RunIds.Contains(run.Id)))
        {
            var associated = GetRunPullRequests(run, openByHeadSha, out var ambiguous);
            if (ambiguous && options.RunIds.Contains(run.Id))
                throw new InvalidDataException($"Pinned run {run.Id} matches multiple open pull requests.");
            if (associated.Length == 0 && !ambiguous && options.RunIds.Contains(run.Id)
                && run.PullRequests is not { Length: > 0 })
                associated = await GetPinnedRunPullRequestsAsync(
                    run, pullRequests, commitHistory, pullRequestHistory, isVerified, cancellationToken);
            builds.AddRange(associated.Where(openPullRequests.Contains).Select(pr => ToBuild(run, pr)));
        }
        return new BuildDiscovery(openPullRequests, builds);
    }

    public async Task<SourceBuild> LoadArtifactsAsync(SourceBuild build, CancellationToken cancellationToken) =>
        build with { Artifacts = await github.GetArtifactsAsync(build.RunId, cancellationToken) };

    public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken) =>
        github.DownloadAsync(artifact, destination, cancellationToken);

    private SourceBuild ToBuild(WorkflowRun run, int pullRequest) => new(
        Name, pullRequest, run.Id, run.Attempt, run.HeadSha,
        IsSuccessfulPullRequestRun(run) ? BuildCompletion.Succeeded
            : run.Status == "completed" ? BuildCompletion.Failed : BuildCompletion.InProgress,
        []);

    private static bool IsSuccessfulPullRequestRun(WorkflowRun run) =>
        run.Status == "completed" && run.Conclusion == "success" && run.Event == "pull_request";

    private int[] GetRunPullRequests(
        WorkflowRun run, IReadOnlyDictionary<string, PullRequestInfo[]> openByHeadSha, out bool ambiguous)
    {
        ambiguous = false;
        if (run.PullRequests is { Length: > 0 })
            return run.PullRequests.Select(x => x.Number).ToArray();
        if (string.IsNullOrWhiteSpace(run.HeadSha) || !openByHeadSha.TryGetValue(run.HeadSha, out var candidates))
            return [];

        var matches = candidates
            .Where(pr => MatchesWhenKnown(run.HeadRepository?.FullName, pr.Head?.Repository?.FullName,
                StringComparison.OrdinalIgnoreCase))
            .Where(pr => MatchesWhenKnown(run.HeadBranch, pr.Head?.Branch, StringComparison.Ordinal))
            .Select(pr => pr.Number).Distinct().Take(2).ToArray();
        if (matches.Length > 1)
        {
            ambiguous = true;
            _logger.LogWarning("Skipping run {RunId}: its head matches multiple open pull requests.", run.Id);
        }
        return matches.Length == 1 ? matches : [];
    }

    private async Task<int[]> GetPinnedRunPullRequestsAsync(
        WorkflowRun run, IReadOnlyList<PullRequestInfo> pullRequests,
        Dictionary<int, HashSet<string>> commitHistory,
        Dictionary<(string Repository, string Branch), int[]> pullRequestHistory,
        Func<SourceBuild, bool> isVerified,
        CancellationToken cancellationToken)
    {
        var repository = run.HeadRepository?.FullName;
        var branch = run.HeadBranch;
        if (string.IsNullOrWhiteSpace(run.HeadSha) || string.IsNullOrWhiteSpace(repository)
            || string.IsNullOrWhiteSpace(branch))
        {
            _logger.LogWarning("Skipping pinned run {RunId}: GitHub did not supply its head SHA, repository, and branch.", run.Id);
            return [];
        }

        var candidates = pullRequests
            .Where(pr => string.Equals(repository, pr.Head?.Repository?.FullName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(branch, pr.Head?.Branch, StringComparison.Ordinal))
            .DistinctBy(pr => pr.Number).Take(2).ToArray();
        if (candidates.Length == 0)
        {
            _logger.LogWarning("Skipping pinned run {RunId}: no open pull request has the same head repository and branch.", run.Id);
            return [];
        }
        if (candidates.Length > 1)
            throw new InvalidDataException($"Pinned run {run.Id} matches multiple open pull requests with the same head repository and branch.");

        var pullRequest = candidates[0].Number;
        if (isVerified(ToBuild(run, pullRequest))) return [pullRequest];

        var head = (repository, branch);
        if (!pullRequestHistory.TryGetValue(head, out var historicalPullRequests))
        {
            historicalPullRequests = (await github.GetPullRequestsForHeadAsync(repository, branch, cancellationToken))
                .Select(pr => pr.Number).Distinct().Take(2).ToArray();
            pullRequestHistory.Add(head, historicalPullRequests);
        }
        if (historicalPullRequests.Length != 1 || historicalPullRequests[0] != pullRequest)
            throw new InvalidDataException(
                $"Pinned run {run.Id} cannot be associated with pull request {pullRequest}: the head repository and branch have an ambiguous pull-request history.");

        if (!commitHistory.TryGetValue(pullRequest, out var commits))
        {
            commits = (await github.GetPullRequestCommitShasAsync(pullRequest, cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            commitHistory.Add(pullRequest, commits);
        }
        if (!commits.Contains(run.HeadSha))
            throw new InvalidDataException(
                $"Pinned run {run.Id} cannot be associated with pull request {pullRequest}: its head SHA is absent from the current PR commit history. The branch may have been rewritten or reused.");
        return [pullRequest];
    }

    private static bool MatchesWhenKnown(string? first, string? second, StringComparison comparison) =>
        string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)
            || string.Equals(first, second, comparison);
}
