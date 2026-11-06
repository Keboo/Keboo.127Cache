using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using Keboo._127Cache;

namespace Keboo._127Cache.Tests;

internal sealed class OfflineFakeGitHub(string directory) : IGitHubClient
{
    private readonly Dictionary<long, (Artifact Version, Artifact Packages)> _artifacts = [];
    private readonly Dictionary<long, string> _files = [];
    private readonly ConcurrentQueue<long> _artifactQueries = new();
    private int _discoveryCalls;
    private int _downloadCalls;

    public PullRequestInfo[] OpenPullRequests { get; set; } = [];
    public WorkflowRun[] Runs { get; set; } = [];
    public HashSet<long> ExpiredRuns { get; } = [];
    public Exception? DiscoveryError { get; set; }
    public long? FailArtifactsForRun { get; set; }
    public long? MissingVersionArtifactForRun { get; set; }
    public bool FailDownloads { get; set; }
    public TaskCompletionSource? DiscoveryBlock { get; set; }
    public int DiscoveryCalls => Volatile.Read(ref _discoveryCalls);
    public int DownloadCalls => Volatile.Read(ref _downloadCalls);
    public long[] ArtifactQueries => _artifactQueries.ToArray();

    public WorkflowRun AddBuild(long runId, int pullRequest, params string[] packageIds)
    {
        var fixture = Path.Combine(directory, "source-" + runId);
        Directory.CreateDirectory(fixture);
        var versionPath = Path.Combine(fixture, "version.zip");
        using (var zip = ZipFile.Open(versionPath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("version.txt").Open()))
            writer.Write("1.2.3");

        var packagesPath = Path.Combine(fixture, "packages.zip");
        using (var zip = ZipFile.Open(packagesPath, ZipArchiveMode.Create))
        {
            foreach (var id in packageIds)
            {
                var path = Path.Combine(fixture, id + ".nupkg");
                WritePackage(path, id);
                zip.CreateEntryFromFile(path, "../untrusted/" + id + ".nupkg");
            }
        }

        var version = ArtifactFor(runId * 10 + 1, "build-version", versionPath);
        var packages = ArtifactFor(runId * 10 + 2, "packages", packagesPath);
        _artifacts.Add(runId, (version, packages));
        _files.Add(version.Id, versionPath);
        _files.Add(packages.Id, packagesPath);
        return new WorkflowRun(runId, 1, "head-" + runId, "completed", "success", "pull_request", [new(pullRequest)]);
    }

    public Artifact VersionArtifact(long runId) => _artifacts[runId].Version;
    public Artifact PackagesArtifact(long runId) => _artifacts[runId].Packages;
    public string ArtifactFile(long artifactId) => _files[artifactId];

    public void AddArtifactMetadata(long runId, Artifact version, Artifact packages) =>
        _artifacts[runId] = (version, packages);

    public void UseDigestlessArtifacts(long runId)
    {
        var (version, packages) = _artifacts[runId];
        _artifacts[runId] = (version with { Digest = null }, packages with { Digest = null });
    }

    public async Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _discoveryCalls);
        if (DiscoveryBlock is { } block) await block.Task.WaitAsync(cancellationToken);
        if (DiscoveryError is { } error) throw error;
        return OpenPullRequests;
    }

    public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowRun>>(Runs);

    public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken)
    {
        _artifactQueries.Enqueue(runId);
        if (FailArtifactsForRun == runId) throw new IOException("Synthetic artifact discovery failure.");
        var (version, packages) = _artifacts[runId];
        var expired = ExpiredRuns.Contains(runId);
        if (MissingVersionArtifactForRun == runId)
            return Task.FromResult<IReadOnlyList<Artifact>>([packages with { Expired = expired }]);
        return Task.FromResult<IReadOnlyList<Artifact>>([version with { Expired = expired }, packages with { Expired = expired }]);
    }

    public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _downloadCalls);
        if (FailDownloads) throw new IOException("Synthetic download failure.");
        File.Copy(_files[artifact.Id], destination);
        return Task.CompletedTask;
    }

    private static Artifact ArtifactFor(long id, string name, string path) =>
        new(id, name, false, "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());

    private static void WritePackage(string path, string id)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open()))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>{id}</id><version>1.2.3</version><authors>offline-test</authors>
                    <description>Offline fixture for {id}</description></metadata>
                </package>
                """);
        }
        zip.CreateEntry("lib/net8.0/_._");
        using var content = zip.CreateEntry("lib/net8.0/payload.txt").Open();
        content.Write(Encoding.UTF8.GetBytes("payload-" + id));
    }
}