using System.IO.Compression;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using NuGet.Versioning;

namespace Keboo._127Cache;

public sealed record BuildEntry(
    long RunId, int Attempt, int PullRequest, string HeadSha, string OriginalVersion, string Version,
    Artifact Packages, Artifact VersionArtifact);
public sealed record CatalogFile(string Repository, List<BuildEntry> Builds, int SchemaVersion = 1);
public sealed record PublishedPackage(string Id, BuildEntry Build);
public sealed record RunArtifacts(
    long RunId, int Attempt, int PullRequest, string HeadSha, Artifact Packages, Artifact VersionArtifact);
public sealed record RunArtifactsFile(string Repository, string Workflow, List<RunArtifacts> Runs);

public sealed class PackageCatalog
{
    private readonly ProxyOptions _options;
    private readonly IGitHubClient _github;
    private readonly ILogger<PackageCatalog> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly ArtifactCache _artifacts;
    private readonly PackageCache _packages;
    private readonly object _cacheKey = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _file;
    private readonly string _runsFile;
    private List<RunArtifacts> _runs;
    private List<BuildEntry> _builds;
    private bool _catalogNeedsUpgrade;

    public PackageCatalog(ProxyOptions options, IGitHubClient github, IMemoryCache memoryCache, ArtifactCache artifacts, PackageCache packages,
        ILogger<PackageCatalog>? logger = null)
    {
        _options = options;
        _github = github;
        _logger = logger ?? NullLogger<PackageCatalog>.Instance;
        _memoryCache = memoryCache;
        _artifacts = artifacts;
        _packages = packages;
        _file = Path.Combine(options.CacheDirectory, "catalog.json");
        Directory.CreateDirectory(options.CacheDirectory);
        var saved = File.Exists(_file)
            ? JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(_file), JsonSettings.Options)
                ?? throw new InvalidDataException("Invalid saved catalog.")
            : null;
        if (saved is not null && saved.Repository != options.Repository)
            throw new InvalidDataException("This cache belongs to another repository. Use a different CacheDirectory.");
        if (saved is not null && saved.SchemaVersion > CacheSchema.Version)
            throw new InvalidDataException("This cache was created by a newer version. Use a different CacheDirectory or upgrade 127Cache.");
        _builds = saved?.Builds.Select(build => build with
        {
            Version = PackageRewriter.VersionFor(build.OriginalVersion, build.PullRequest, build.RunId, build.Attempt, build.Packages.Id)
        }).ToList() ?? [];
        _catalogNeedsUpgrade = saved is not null
            && (saved.SchemaVersion != CacheSchema.Version || !saved.Builds.SequenceEqual(_builds));
        _runsFile = Path.Combine(options.CacheDirectory, "runs.json");
        var savedRuns = File.Exists(_runsFile)
            ? JsonSerializer.Deserialize<RunArtifactsFile>(File.ReadAllText(_runsFile), JsonSettings.Options)
                ?? throw new InvalidDataException("Invalid saved run metadata.")
            : null;
        if (savedRuns is not null && (savedRuns.Repository != options.Repository || savedRuns.Workflow != options.Workflow))
            throw new InvalidDataException("This run cache belongs to another repository/workflow. Use a different CacheDirectory.");
        _runs = savedRuns?.Runs ?? _builds
            .Where(x => x.VersionArtifact is not null)
            .Select(x => new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact))
            .ToList();
    }

    public async Task<PublishedPackage[]> GetPackagesAsync(CancellationToken cancellationToken)
    {
        var result = new List<PublishedPackage>();
        foreach (var build in await GetBuildsAsync(cancellationToken))
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
            catch (Exception exception) when (IsBuildFailure(exception))
            {
                ReportUnavailableBuild(build, exception);
            }
        }
        return result.OrderBy(x => NuGetVersion.Parse(x.Build.Version), PackageRewriter.VersionOrder).ToArray();
    }

    public async Task<BuildEntry[]> GetBuildsAsync(CancellationToken cancellationToken)
    {
        var runs = await GetRunArtifactsAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = new List<BuildEntry>(_builds);
            var unavailable = new HashSet<BuildEntry>();
            foreach (var run in runs)
            {
                if (next.Any(x => x.RunId == run.RunId && x.Attempt == run.Attempt && x.PullRequest == run.PullRequest)) continue;
                try
                {
                    var original = await ReadVersionAsync(run.VersionArtifact, cancellationToken);
                    next.Add(new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, original,
                        PackageRewriter.VersionFor(original, run.PullRequest, run.RunId, run.Attempt, run.Packages.Id),
                        run.Packages, run.VersionArtifact));
                }
                catch (Exception exception) when (IsBuildFailure(exception))
                {
                    _logger.LogWarning(exception, "Skipping run {RunId} attempt {Attempt} of PR {PullRequest}: " +
                        "version artifact {ArtifactId} is unavailable.", run.RunId, run.Attempt, run.PullRequest, run.VersionArtifact.Id);
                }
            }
            if (_catalogNeedsUpgrade)
            {
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
            else if (next.Count != _builds.Count)
                await JsonSettings.WriteAtomicAsync(_file, new CatalogFile(_options.Repository, next, CacheSchema.Version), cancellationToken);
            _builds = next;
            var visible = runs.Select(x => (x.RunId, x.Attempt, x.PullRequest)).ToHashSet();
            var candidates = _builds.Where(x => visible.Contains((x.RunId, x.Attempt, x.PullRequest)) && !unavailable.Contains(x))
                .OrderBy(x => NuGetVersion.Parse(x.Version), PackageRewriter.VersionOrder).ToArray();
            var available = new List<BuildEntry>(candidates.Length);
            var unavailablePullRequests = new HashSet<int>();
            foreach (var build in candidates)
            {
                try
                {
                    await _packages.GetPackagesAsync(build, cancellationToken);
                    available.Add(build);
                }
                catch (Exception exception) when (IsBuildFailure(exception))
                {
                    unavailablePullRequests.Add(build.PullRequest);
                    ReportUnavailableBuild(build, exception);
                }
            }
            foreach (var pullRequest in unavailablePullRequests)
            {
                var fallbacks = _builds
                    .Where(x => x.PullRequest == pullRequest
                        && !visible.Contains((x.RunId, x.Attempt, x.PullRequest)))
                    .OrderByDescending(x => x.RunId).ThenByDescending(x => x.Attempt);
                foreach (var fallback in fallbacks)
                {
                    try
                    {
                        await _packages.GetPackagesAsync(fallback, cancellationToken);
                        available.Add(fallback);
                        break;
                    }
                    catch (Exception exception) when (IsBuildFailure(exception))
                    {
                        ReportUnavailableBuild(fallback, exception);
                    }
                }
            }
            available.Sort((left, right) =>
                PackageRewriter.VersionOrder.Compare(NuGetVersion.Parse(left.Version), NuGetVersion.Parse(right.Version)));
            return available.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RunArtifacts[]> GetRunArtifactsAsync(CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(_cacheKey, out RunArtifacts[]? cached) && cached is not null) return cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache.TryGetValue(_cacheKey, out cached) && cached is not null) return cached;
            var pullRequests = await _github.GetOpenPullRequestsAsync(cancellationToken);
            var openPullRequests = pullRequests.Select(x => x.Number).ToHashSet();
            var openByHeadSha = pullRequests
                .Where(x => !string.IsNullOrWhiteSpace(x.Head?.Sha))
                .GroupBy(x => x.Head!.Sha!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var discovered = openPullRequests.Count == 0 ? [] : await _github.GetRunsAsync(cancellationToken);
            var associatedRuns = new List<(WorkflowRun Run, int PullRequest)>();
            var commitHistory = new Dictionary<int, HashSet<string>>();
            var pullRequestHistory = new Dictionary<(string Repository, string Branch), int[]>();
            foreach (var run in discovered.Where(run => IsSuccessfulPullRequestRun(run) || _options.RunIds.Contains(run.Id)))
            {
                var associated = GetRunPullRequests(run, openByHeadSha, out var ambiguous);
                if (ambiguous && _options.RunIds.Contains(run.Id))
                    throw new InvalidDataException($"Pinned run {run.Id} matches multiple open pull requests.");
                if (associated.Length == 0 && !ambiguous && _options.RunIds.Contains(run.Id)
                    && run.PullRequests is not { Length: > 0 })
                    associated = await GetPinnedRunPullRequestsAsync(run, pullRequests, commitHistory, pullRequestHistory, cancellationToken);
                associatedRuns.AddRange(associated.Where(openPullRequests.Contains).Select(pr => (run, pr)));
            }
            var eligibleRuns = associatedRuns
                .Where(x => IsSuccessfulPullRequestRun(x.Run))
                .Where(x => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(x.PullRequest)
                    || _options.RunIds.Contains(x.Run.Id))
                .ToArray();
            var runs = eligibleRuns.GroupBy(x => x.PullRequest)
                .SelectMany(g => g.OrderByDescending(x => x.Run.Id).Take(1).Concat(g.Where(x => _options.RunIds.Contains(x.Run.Id))))
                .DistinctBy(x => (x.Run.Id, x.Run.Attempt, x.PullRequest));
            // Keep immutable completed-run and artifact metadata on disk even while a PR is closed.
            // Open-PR membership is volatile and is applied only to the response below.
            var next = new List<RunArtifacts>(_runs);
            foreach (var (run, pr) in runs)
            {
                // Completed immutable builds already known locally need no artifact API/download request.
                if (next.Any(x => x.RunId == run.Id && x.Attempt == run.Attempt && x.PullRequest == pr)) continue;
                var artifacts = await _github.GetArtifactsAsync(run.Id, cancellationToken);
                var packages = artifacts.SingleOrDefault(x => x.Name == _options.PackagesArtifact);
                var version = artifacts.SingleOrDefault(x => x.Name == _options.VersionArtifact);
                if (packages is null || version is null)
                {
                    if (_options.RunIds.Contains(run.Id))
                        throw new InvalidDataException($"Run {run.Id} has missing package/version artifacts.");
                    _logger.LogWarning("Skipping run {RunId}: missing package/version artifacts.", run.Id);
                    continue;
                }
                if ((packages.Expired && !_artifacts.Contains(packages)) || (version.Expired && !_artifacts.Contains(version)))
                {
                    if (_options.RunIds.Contains(run.Id))
                        throw new InvalidDataException($"Run {run.Id} has expired artifacts that are not cached locally.");
                    _logger.LogWarning("Skipping run {RunId}: expired artifacts are not cached locally.", run.Id);
                    continue;
                }
                next.Add(new RunArtifacts(run.Id, run.Attempt, pr, run.HeadSha, packages, version));
            }
            foreach (var id in _options.RunIds)
            {
                if (associatedRuns.Any(x => x.Run.Id == id)
                    && !next.Any(x => x.RunId == id))
                    throw new InvalidDataException($"Run {id} is not an available successful build of an open pull request.");
            }
            await JsonSettings.WriteAtomicAsync(_runsFile, new RunArtifactsFile(_options.Repository, _options.Workflow, next), cancellationToken);
            _runs = next;
            var result = next.Where(x => openPullRequests.Contains(x.PullRequest))
                .Where(x => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(x.PullRequest)
                    || _options.RunIds.Contains(x.RunId))
                .OrderBy(x => x.RunId).ThenBy(x => x.Attempt).ToArray();
            return _memoryCache.Set(_cacheKey, result, TimeSpan.FromSeconds(_options.RefreshSeconds));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsBuildFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException
            or System.Xml.XmlException or NuGet.Packaging.Core.PackagingException;

    private void ReportUnavailableBuild(BuildEntry build, Exception exception) =>
        _logger.LogWarning(exception, "Skipping build {RunId} attempt {Attempt} of PR {PullRequest}: " +
            "package artifact {ArtifactId} is unavailable or invalid; existing cache files were retained.",
            build.RunId, build.Attempt, build.PullRequest, build.Packages.Id);

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
        if (_runs.Any(x => x.RunId == run.Id && x.Attempt == run.Attempt && x.PullRequest == pullRequest
            && string.Equals(x.HeadSha, run.HeadSha, StringComparison.OrdinalIgnoreCase)))
            return [pullRequest];

        var head = (repository, branch);
        if (!pullRequestHistory.TryGetValue(head, out var historicalPullRequests))
        {
            historicalPullRequests = (await _github.GetPullRequestsForHeadAsync(repository, branch, cancellationToken))
                .Select(pr => pr.Number).Distinct().Take(2).ToArray();
            pullRequestHistory.Add(head, historicalPullRequests);
        }
        if (historicalPullRequests.Length != 1 || historicalPullRequests[0] != pullRequest)
            throw new InvalidDataException(
                $"Pinned run {run.Id} cannot be associated with pull request {pullRequest}: the head repository and branch have an ambiguous pull-request history.");

        if (!commitHistory.TryGetValue(pullRequest, out var commits))
        {
            commits = (await _github.GetPullRequestCommitShasAsync(pullRequest, cancellationToken))
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

    private async Task<string> ReadVersionAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var path = await _artifacts.GetAsync(artifact, cancellationToken);
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