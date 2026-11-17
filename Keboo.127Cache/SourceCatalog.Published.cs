using System.Text.Json;

namespace Keboo.LocalCache;

internal sealed partial class SourceCatalog
{
    private sealed record PublishedItem(PublishedPackage Package, string Path);
    private sealed record PublishedFile(int SchemaVersion, string Repository, string Workflow,
        string PackagesArtifact, string? VersionArtifact, int[] PullRequests, long[] RunIds, PublishedPackage[] Packages);

    private sealed class PublishedSnapshot(PublishedItem[] items)
    {
        public PublishedItem[] Items { get; } = items;
        public int Readers { get; set; }
        public TaskCompletionSource Drained { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _publishedGate = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private PublishedSnapshot _published = new([]);
    private readonly List<PublishedSnapshot> _retiredSnapshots = [];
    private bool _restoredPublished;
    private string PublishedFilePath => Path.Combine(_options.CacheDirectory, "published-packages.json");
    internal event Action? PublishedChanged;

    private PublishedSnapshot AcquireSnapshot()
    {
        lock (_publishedGate)
        {
            if (_published.Readers == 0)
                _published.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _published.Readers++;
            return _published;
        }
    }

    private void ReleaseSnapshot(PublishedSnapshot snapshot)
    {
        lock (_publishedGate)
        {
            if (--snapshot.Readers == 0)
            {
                snapshot.Drained.TrySetResult();
                _retiredSnapshots.Remove(snapshot);
            }
        }
    }

    private void Publish(PublishedItem[] items)
    {
        lock (_publishedGate)
        {
            var old = _published;
            _published = new PublishedSnapshot(items);
            if (old.Readers != 0) _retiredSnapshots.Add(old);
        }
        PublishedChanged?.Invoke();
    }

    private async Task PublishPackagesAsync(PublishedPackage[] packages, CancellationToken token)
    {
        var items = new List<PublishedItem>(packages.Length);
        foreach (var package in packages)
        {
            var path = await _packages.GetAsync(package.Id, package.Build, token);
            items.Add(new PublishedItem(package, path));
        }
        var ready = items.ToArray();
        await SavePublishedAsync(ready, token);
        Publish(ready);
    }

    private Task SavePublishedAsync(PublishedItem[] items, CancellationToken token) =>
        JsonSettings.WriteAtomicAsync(PublishedFilePath,
            new PublishedFile(1, _options.Repository, _options.Workflow, _options.PackagesArtifact,
                _options.VersionArtifact, _options.PullRequests.Distinct().Order().ToArray(),
                _options.RunIds.Distinct().Order().ToArray(), items.Select(x => x.Package).ToArray()), token);

    private async Task RestorePublishedAsync(CancellationToken token)
    {
        if (_restoredPublished) return;
        _restoredPublished = true;
        if (!File.Exists(PublishedFilePath))
        {
            var fallback = FindFallback("Initial discovery is in progress.");
            if (fallback is null) return;
            var items = new List<PublishedItem>();
            foreach (var run in fallback.Runs)
            {
                var build = new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, "", "",
                    run.Packages, run.VersionArtifact);
                var available = await _packages.GetAvailableOfflineAsync(build, token, allowRepair: false);
                foreach (var cached in available.Packages)
                {
                    var path = _packages.FindVerifiedPath(build, cached);
                    if (path is not null)
                        items.Add(new PublishedItem(new PublishedPackage(cached.Id, build with
                        {
                            OriginalVersion = cached.OriginalVersion,
                            Version = cached.Version
                        }), path));
                }
            }
            if (items.Count > 0) Publish(items.ToArray());
            return;
        }
        try
        {
            var file = JsonSerializer.Deserialize<PublishedFile>(
                await File.ReadAllTextAsync(PublishedFilePath, token), JsonSettings.Options);
            if (file is null || file.SchemaVersion != 1 || file.Packages is null
                || file.Repository != _options.Repository || file.Workflow != _options.Workflow
                || file.PackagesArtifact != _options.PackagesArtifact || file.VersionArtifact != _options.VersionArtifact
                || file.PullRequests is null || file.RunIds is null
                || !file.PullRequests.SequenceEqual(_options.PullRequests.Distinct().Order())
                || !file.RunIds.SequenceEqual(_options.RunIds.Distinct().Order()))
                throw new InvalidDataException("Published package snapshot does not match the configured source.");
            var items = new List<PublishedItem>();
            string? missing = null;
            var fallback = FindFallback("Initial discovery is in progress.");
            foreach (var group in file.Packages.GroupBy(x => x.Build))
            {
                var matchingRun = fallback?.Runs.Any(run => run.RunId == group.Key.RunId
                    && run.Attempt == group.Key.Attempt && run.PullRequest == group.Key.PullRequest
                    && run.Packages == group.Key.Packages && run.VersionArtifact == group.Key.VersionArtifact) == true;
                if (_options.RunIds.Contains(group.Key.RunId) && !matchingRun)
                {
                    missing ??= "Published package provenance could not be verified against the cached discovery state.";
                    continue;
                }
                var available = await _packages.GetAvailableOfflineAsync(group.Key, token, allowRepair: false);
                foreach (var package in group)
                {
                    var verified = available.Packages.FirstOrDefault(x => x.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                        && x.Version == package.Build.Version && x.OriginalVersion == package.Build.OriginalVersion);
                    var path = verified is null ? null : _packages.FindVerifiedPath(package.Build, verified);
                    if (path is null)
                        missing ??= $"Published package {package.Id} is missing or failed verification.";
                    else
                        items.Add(new PublishedItem(package, path));
                }
            }
            Publish(items.ToArray());
            if (missing is not null)
            {
                _logger.LogWarning("{Error}", missing);
                SetFailureHealth(missing, FindFallback(missing), contentFailure: true);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Published package snapshot for source '{Source}' could not be restored.", Name);
            SetFailureHealth(DiscoveryFailure.Describe(exception), FindFallback(exception.Message), contentFailure: true);
        }
    }

    private PublishedPackage[] PublishedPackages() =>
        (_requestScope.Value?.Snapshot ?? Volatile.Read(ref _published)).Items.Select(x => x.Package).ToArray();

    internal bool HasPublishedBuild(int pullRequest, long runId, int attempt) =>
        Volatile.Read(ref _published).Items.Any(x => x.Package.Build.PullRequest == pullRequest
            && x.Package.Build.RunId == runId && x.Package.Build.Attempt == attempt);

    internal string PublishedPath(string id, BuildEntry build)
    {
        var snapshot = _requestScope.Value?.Snapshot ?? Volatile.Read(ref _published);
        var item = snapshot.Items.FirstOrDefault(x => x.Package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            && x.Package.Build.RunId == build.RunId && x.Package.Build.Attempt == build.Attempt
            && x.Package.Build.PullRequest == build.PullRequest && x.Package.Build.Version == build.Version);
        return item?.Path ?? throw new FileNotFoundException("Package is not in the published snapshot.");
    }

    // Remove visibility before touching the files, then wait for requests using any older generation.
    private async Task<PublishedItem[]> RetirePublishedAsync(Func<BuildEntry, bool> remove, CancellationToken token)
    {
        Task[] readers;
        PublishedItem[] original;
        lock (_publishedGate)
        {
            var old = _published;
            original = old.Items;
            _published = new PublishedSnapshot(old.Items.Where(x => !remove(x.Package.Build)).ToArray());
            if (old.Readers != 0) _retiredSnapshots.Add(old);
            readers = _retiredSnapshots.Select(x => x.Drained.Task).ToArray();
        }
        PublishedChanged?.Invoke();
        await Task.WhenAll(readers).WaitAsync(token);
        return original;
    }

    internal async Task DrainPublishedAsync(CancellationToken token) => await RetirePublishedAsync(_ => true, token);
}
