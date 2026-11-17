using System.Text.Json;

namespace Keboo.LocalCache;

internal sealed partial class SourceCatalog
{
    private sealed record ClosedPullRequestsFile(int SchemaVersion, Dictionary<int, DateTimeOffset> PullRequests);

    private sealed class Footprint
    {
        public HashSet<long> PackageIds { get; } = [];
        public HashSet<long> VersionIds { get; } = [];
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool InCatalog { get; set; }
    }

    private readonly object _closedGate = new();
    private readonly Dictionary<int, DateTimeOffset> _closedSince = [];
    private readonly string _closedFile;
    private int _catalogSchema = CacheSchema.Version;

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    // The first time this cache saw a pull request closed is the only closure time available from IBuildSource.
    private void LoadClosedPullRequests()
    {
        try
        {
            if (!File.Exists(_closedFile)) return;
            var saved = JsonSerializer.Deserialize<ClosedPullRequestsFile>(File.ReadAllText(_closedFile), JsonSettings.Options);
            if (saved?.PullRequests is null) return;
            foreach (var (pullRequest, since) in saved.PullRequests) _closedSince[pullRequest] = since;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Closed pull request times could not be read; closure is measured from now: {Error}", exception.Message);
        }
    }

    private void TrackClosedPullRequests(IReadOnlySet<int>? open)
    {
        var known = CollectFootprints().Keys.Select(x => x.PullRequest).ToHashSet();
        Dictionary<int, DateTimeOffset> snapshot;
        lock (_closedGate)
        {
            var changed = false;
            // Without the open set (after a removal) entries are only pruned for pull requests that are gone.
            if (open is not null)
                foreach (var pullRequest in known)
                    if (!open.Contains(pullRequest) && !_closedSince.ContainsKey(pullRequest))
                    {
                        _closedSince[pullRequest] = Now;
                        changed = true;
                    }
            foreach (var pullRequest in _closedSince.Keys.ToArray())
                if (open?.Contains(pullRequest) == true || !known.Contains(pullRequest))
                    changed |= _closedSince.Remove(pullRequest);
            if (!changed) return;
            snapshot = new Dictionary<int, DateTimeOffset>(_closedSince);
        }
        // Synchronous on purpose: discovery refreshes that complete synchronously must stay that way.
        var temporary = _closedFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ClosedPullRequestsFile(1, snapshot), JsonSettings.Options));
            File.Move(temporary, _closedFile, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Closed pull request times could not be saved: {Error}", exception.Message);
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private Dictionary<BuildKey, Footprint> CollectFootprints()
    {
        var result = new Dictionary<BuildKey, Footprint>();
        Footprint Get(BuildKey key)
        {
            if (!result.TryGetValue(key, out var footprint)) result[key] = footprint = new Footprint();
            return footprint;
        }
        void AddRun(int pullRequest, long runId, int attempt, Artifact packages, Artifact? version)
        {
            var footprint = Get(new BuildKey(pullRequest, runId, attempt));
            footprint.PackageIds.Add(packages.Id);
            if (version is not null) footprint.VersionIds.Add(version.Id);
        }

        foreach (var build in _builds)
        {
            AddRun(build.PullRequest, build.RunId, build.Attempt, build.Packages, build.VersionArtifact);
            Get(new BuildKey(build.PullRequest, build.RunId, build.Attempt)).InCatalog = true;
        }
        foreach (var run in _runs) AddRun(run.PullRequest, run.RunId, run.Attempt, run.Packages, run.VersionArtifact);
        foreach (var run in _snapshot?.Runs ?? []) AddRun(run.PullRequest, run.RunId, run.Attempt, run.Packages, run.VersionArtifact);
        foreach (var run in Volatile.Read(ref _partialDiscovery)?.Discovery.Runs ?? [])
            AddRun(run.PullRequest, run.RunId, run.Attempt, run.Packages, run.VersionArtifact);
        foreach (var (key, artifactId, path) in CacheDisk.ScanBuildEntries(_options.CacheDirectory))
        {
            var footprint = Get(key);
            footprint.PackageIds.Add(artifactId);
            footprint.Paths.Add(path);
        }
        return result;
    }

    private IEnumerable<string> ArtifactPaths(Footprint footprint, HashSet<long> keptIds) =>
        footprint.PackageIds.Concat(footprint.VersionIds).Where(x => !keptIds.Contains(x)).Distinct()
            .SelectMany(id => CacheDisk.ArtifactFiles(_options.CacheDirectory, id));

    private (long Artifact, long Package, bool Partial) Measure(Footprint footprint, HashSet<long> keptIds, HashSet<string> counted)
    {
        long artifactBytes = 0, packageBytes = 0;
        var partial = false;
        foreach (var path in ArtifactPaths(footprint, keptIds))
            if (counted.Add(path)) artifactBytes += CacheDisk.SizeOf(path);
        foreach (var path in footprint.Paths)
        {
            if (!counted.Add(path)) continue;
            packageBytes += CacheDisk.SizeOf(path);
            if (Directory.Exists(path)
                && (!File.Exists(Path.Combine(path, "packages.json")) || !File.Exists(Path.Combine(path, "integrity.json"))))
                partial = true;
        }
        return (artifactBytes, packageBytes, partial);
    }

    internal Task<SourceDiskUsage> GetDiskUsageAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var footprints = CollectFootprints();
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builds = new List<BuildDiskUsage>();
        foreach (var (key, footprint) in footprints.OrderBy(x => x.Key.PullRequest).ThenBy(x => x.Key.RunId).ThenBy(x => x.Key.Attempt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (artifactBytes, packageBytes, partial) = Measure(footprint, [], counted);
            if (artifactBytes + packageBytes == 0 && footprint.Paths.Count == 0) continue;
            DateTimeOffset? closedSince;
            lock (_closedGate) closedSince = _closedSince.TryGetValue(key.PullRequest, out var since) ? since : null;
            builds.Add(new BuildDiskUsage(Name, key.PullRequest, key.RunId, key.Attempt, artifactBytes, packageBytes, closedSince, partial,
                CachedSince(footprint.Paths)));
        }
        var total = CacheDisk.SizeOf(_options.CacheDirectory);
        return new SourceDiskUsage(Name, total, Math.Max(0, total - builds.Sum(x => x.TotalBytes)), builds);
    }, cancellationToken);

    // The oldest write time among the build's cached package entries approximates when it was cached.
    private static DateTimeOffset? CachedSince(IEnumerable<string> paths)
    {
        DateTimeOffset? oldest = null;
        foreach (var path in paths)
        {
            var written = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path)
                : File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
            if (written is { } value && (oldest is null || value < oldest)) oldest = new DateTimeOffset(value, TimeSpan.Zero);
        }
        return oldest;
    }

    private IEnumerable<string> SweepEntries()
    {
        var directory = _options.CacheDirectory;
        foreach (var path in CacheDisk.SafeEnumerate(Path.Combine(directory, "artifacts"), directories: false)) yield return path;
        foreach (var path in CacheDisk.SafeEnumerate(Path.Combine(directory, "packages", "discovered"), directories: true)) yield return path;
        foreach (var path in CacheDisk.SafeEnumerate(Path.Combine(directory, "inventories"), directories: false)) yield return path;
        foreach (var path in CacheDisk.SafeEnumerate(directory, directories: true, pattern: "staging-*")) yield return path;
    }

    private async Task<ReappearanceOutcome> ClassifyAsync(BuildKey key, BuildDiscovery? live, CancellationToken cancellationToken)
    {
        if (live is null) return ReappearanceOutcome.Unknown;
        if (!live.OpenPullRequests.Contains(key.PullRequest)) return ReappearanceOutcome.PullRequestClosed;
        var eligible = live.Builds
            .Where(x => x.State == BuildCompletion.Succeeded && x.PullRequest == key.PullRequest)
            .Where(x => _options.PullRequests.Length == 0 || _options.PullRequests.Contains(x.PullRequest)
                || _options.RunIds.Contains(x.RunId))
            .ToArray();
        var run = eligible.FirstOrDefault(x => x.RunId == key.RunId && x.Attempt == key.Attempt);
        if (run is null) return ReappearanceOutcome.NotAvailableUpstream;
        var latest = eligible.OrderByDescending(x => x.RunId).ThenByDescending(x => x.Attempt).First();
        if (latest != run && !_options.RunIds.Contains(run.RunId)) return ReappearanceOutcome.SupersededByNewerRun;
        IReadOnlyList<Artifact> artifacts;
        try
        {
            artifacts = (await _source.LoadArtifactsAsync(run, cancellationToken)).Artifacts;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ReappearanceOutcome.Unknown;
        }
        var packages = artifacts.Where(x => x.Name == _options.PackagesArtifact).Take(2).ToArray();
        var versions = _options.VersionArtifact is null ? [] : artifacts.Where(x => x.Name == _options.VersionArtifact).Take(2).ToArray();
        if (packages.Length != 1 || (_options.VersionArtifact is not null && versions.Length != 1))
            return ReappearanceOutcome.NotAvailableUpstream;
        return packages[0].Expired || versions.Any(x => x.Expired)
            ? ReappearanceOutcome.ArtifactExpired : ReappearanceOutcome.WillReappear;
    }

    private bool IsRemovable(RemovalScope scope, BuildKey key, BuildDiscovery? live)
    {
        if (!scope.Match(key)) return false;
        if (!scope.OnlyClosedPullRequests) return true;
        if (live is null || live.OpenPullRequests.Contains(key.PullRequest)) return false;
        if (scope.ClosedFor is not { } age || age == TimeSpan.Zero) return true;
        lock (_closedGate)
            return _closedSince.TryGetValue(key.PullRequest, out var since) && Now - since >= age;
    }

    internal async Task<SourceRemoval> RemoveAsync(RemovalScope scope, bool dryRun, CancellationToken cancellationToken)
    {
        if (!_options.HasSource) return SourceRemoval.Empty;
        BuildDiscovery? live = null;
        try
        {
            live = await _source.DiscoverAsync(build => _runs.Any(x =>
                x.RunId == build.RunId && x.Attempt == build.Attempt && x.PullRequest == build.PullRequest
                && string.Equals(x.HeadSha, build.HeadSha, StringComparison.OrdinalIgnoreCase)), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (scope.OnlyClosedPullRequests)
            {
                _logger.LogWarning("Closed pull requests of source '{Source}' were not removed because GitHub could not be queried: {Error}",
                    Name, DiscoveryFailure.Describe(exception));
                return SourceRemoval.Empty;
            }
        }
        if (live is not null) TrackClosedPullRequests(live.OpenPullRequests);

        var outcomes = new Dictionary<BuildKey, ReappearanceOutcome>();
        foreach (var key in CollectFootprints().Keys.Where(x => IsRemovable(scope, x, live)))
            outcomes[key] = await ClassifyAsync(key, live, cancellationToken);
        if (outcomes.Count == 0 && !scope.SweepSource) return SourceRemoval.Empty;

        await _publicationGate.WaitAsync(cancellationToken);
        try
        {
        await _gate.WaitAsync(cancellationToken);
        IDisposable? locks = null;
        var trash = Path.Combine(_options.CacheDirectory, CacheDisk.TrashPrefix + Guid.NewGuid().ToString("N"));
        var moves = new List<(string Trash, string Original, BuildKey? Key)>();
        PublishedItem[]? originalPublished = null;
        var committed = false;
        try
        {
            var footprints = CollectFootprints();
            var selected = outcomes.Keys.Where(footprints.ContainsKey).ToHashSet();
            var kept = footprints.Where(x => !selected.Contains(x.Key)).SelectMany(x => x.Value.PackageIds.Concat(x.Value.VersionIds))
                .ToHashSet();
            var sweep = scope.SweepSource ? SweepEntries().ToArray() : [];
            var sweepBytes = sweep.Sum(CacheDisk.SizeOf);

            if (!dryRun)
            {
                locks = await _packages.LockForEvictionAsync(
                    selected.SelectMany(key => footprints[key].PackageIds.Select(id => (key, id))),
                    selected.SelectMany(key => footprints[key].PackageIds.Concat(footprints[key].VersionIds)).Where(x => !kept.Contains(x)),
                    cancellationToken);
                // Anything that appeared while waiting for the locks is only visible now.
                footprints = CollectFootprints();
                originalPublished = Volatile.Read(ref _published).Items;
                await SavePublishedAsync(originalPublished.Where(x => !selected.Contains(
                    new BuildKey(x.Package.Build.PullRequest, x.Package.Build.RunId, x.Package.Build.Attempt))).ToArray(),
                    cancellationToken);
                await RetirePublishedAsync(build => selected.Contains(new BuildKey(build.PullRequest, build.RunId, build.Attempt)),
                    cancellationToken);
            }

            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<BuildRemoval>();
            var removed = new HashSet<BuildKey>();
            foreach (var key in selected.Where(footprints.ContainsKey).OrderBy(x => x.PullRequest).ThenBy(x => x.RunId).ThenBy(x => x.Attempt))
            {
                var footprint = footprints[key];
                var paths = footprint.Paths.Concat(ArtifactPaths(footprint, kept)).Distinct().ToArray();
                var (artifactBytes, packageBytes, _) = Measure(footprint, kept, counted);
                var bytes = artifactBytes + packageBytes;
                string? error = null;
                if (!dryRun)
                {
                    Directory.CreateDirectory(trash);
                    var buildMoves = new List<(string Trash, string Original, BuildKey? Key)>();
                    foreach (var path in paths)
                    {
                        var target = Path.Combine(trash, $"{moves.Count + buildMoves.Count}-{Path.GetFileName(path)}");
                        if (await TryMoveAsync(path, target, cancellationToken)) buildMoves.Add((target, path, key));
                        else
                        {
                            error = $"'{Path.GetFileName(path)}' is in use and could not be removed.";
                            break;
                        }
                    }
                    if (error is not null)
                    {
                        await RollbackAsync(buildMoves, cancellationToken);
                        bytes = 0;
                    }
                    else moves.AddRange(buildMoves);
                }
                if (error is null) removed.Add(key);
                if (error is not null || bytes > 0 || footprint.InCatalog)
                    results.Add(new BuildRemoval(key, bytes, outcomes[key], error));
            }

            if (!dryRun)
            {
                if (scope.SweepSource)
                    foreach (var path in SweepEntries().ToArray())
                    {
                        Directory.CreateDirectory(trash);
                        var target = Path.Combine(trash, $"{moves.Count}-{Path.GetFileName(path)}");
                        if (await TryMoveAsync(path, target, cancellationToken)) moves.Add((target, path, null));
                        else _logger.LogWarning("Could not remove '{Path}' from source '{Source}' because it is in use.", path, Name);
                    }
                try
                {
                    await PersistRemovalAsync(removed, cancellationToken);
                }
                catch
                {
                    await RollbackAsync(moves, CancellationToken.None);
                    throw;
                }
                _memoryCache.Remove(_cacheKey);
                lock (_contentRefreshGate) _contentRefresh = null;
                lock (_buildRefreshGate) _buildRefresh = null;
                if (originalPublished is not null)
                {
                    var remainingPublished = originalPublished.Where(x => !removed.Contains(
                        new BuildKey(x.Package.Build.PullRequest, x.Package.Build.RunId, x.Package.Build.Attempt))).ToArray();
                    await SavePublishedAsync(remainingPublished, cancellationToken);
                    Publish(remainingPublished);
                }
            }
            committed = true;
            var unattributed = Math.Max(0, sweepBytes - results.Sum(x => x.BytesReclaimed));
            return new SourceRemoval(results, unattributed);
        }
        finally
        {
            try
            {
                if (!committed && originalPublished is not null)
                {
                    await RollbackAsync(moves, CancellationToken.None);
                    await SavePublishedAsync(originalPublished, CancellationToken.None);
                    Publish(originalPublished);
                }
            }
            finally
            {
                locks?.Dispose();
                _gate.Release();
                if (moves.Count > 0 || Directory.Exists(trash)) CacheDisk.Purge(trash);
            }
        }
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private static async Task<bool> TryMoveAsync(string from, string to, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else if (File.Exists(from)) File.Move(from, to);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) return false;
                await Task.Delay(100, cancellationToken);
            }
        }
        return false;
    }

    private static async Task RollbackAsync(List<(string Trash, string Original, BuildKey? Key)> moves, CancellationToken cancellationToken)
    {
        foreach (var move in Enumerable.Reverse(moves))
            await TryMoveAsync(move.Trash, move.Original, cancellationToken);
        moves.Clear();
    }

    private async Task PersistRemovalAsync(HashSet<BuildKey> removed, CancellationToken cancellationToken)
    {
        if (removed.Count == 0) return;
        bool Keep(int pullRequest, long runId, int attempt) => !removed.Contains(new BuildKey(pullRequest, runId, attempt));

        var runs = _runs.Where(x => Keep(x.PullRequest, x.RunId, x.Attempt)).ToList();
        if (runs.Count != _runs.Count && !_runsUnreadable && File.Exists(_runsFile))
            await JsonSettings.WriteAtomicAsync(_runsFile,
                new RunArtifactsFile(_options.Repository, _options.Workflow, runs, SchemaVersion: 2), cancellationToken);
        var builds = _builds.Where(x => Keep(x.PullRequest, x.RunId, x.Attempt)).ToList();
        if (builds.Count != _builds.Count && !_catalogUnreadable && File.Exists(_file))
        {
            // A catalog that still needs migration keeps its older schema so the remaining builds are verified later.
            var schema = _catalogNeedsUpgrade ? Math.Min(_catalogSchema, CacheSchema.Version - 1) : CacheSchema.Version;
            await JsonSettings.WriteAtomicAsync(_file, new CatalogFile(_options.Repository, builds, schema), cancellationToken);
        }
        var snapshot = _snapshot;
        if (snapshot is not null && snapshot.Runs.Any(x => !Keep(x.PullRequest, x.RunId, x.Attempt)))
        {
            snapshot = snapshot with { Runs = snapshot.Runs.Where(x => Keep(x.PullRequest, x.RunId, x.Attempt)).ToArray() };
            if (File.Exists(_snapshotFile)) await JsonSettings.WriteAtomicAsync(_snapshotFile, snapshot, cancellationToken);
        }
        var partial = Volatile.Read(ref _partialDiscovery);
        if (partial is not null && partial.Discovery.Runs.Any(x => !Keep(x.PullRequest, x.RunId, x.Attempt)))
            await PersistIncompleteAsync(partial.Discovery with
            {
                Runs = partial.Discovery.Runs.Where(x => Keep(x.PullRequest, x.RunId, x.Attempt)).ToArray()
            }, cancellationToken);
        _runs = runs;
        _builds = builds;
        _snapshot = snapshot;
        TrackClosedPullRequests(null);
    }
}
