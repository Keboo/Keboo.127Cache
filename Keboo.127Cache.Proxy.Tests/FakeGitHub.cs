using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Keboo._127Cache.Proxy.Tests;

public sealed class FakeGitHub : IGitHubClient
{
    public const string OriginalVersion = "1.3.67-gabcdef0";
    private readonly string _versionFile;
    private readonly string _packagesFile;
    private readonly string _olderPackagesFile;
    private readonly Artifact _version;
    private readonly Artifact _packages;
    private readonly Artifact _olderPackages;
    public int VersionDownloads { get; private set; }
    public int PackageDownloads { get; private set; }
    public bool CorruptDownload { get; set; }
    public bool Expired { get; set; }
    public bool FailDiscovery { get; set; }
    public bool FailDownloads { get; set; }
    public int OpenPullRequestQueries { get; private set; }
    public int RunQueries { get; private set; }
    public int ArtifactQueries { get; private set; }
    public PullRequestInfo[] OpenPullRequests { get; set; } = [new(755), new(756)];
    public WorkflowRun[]? Runs { get; set; }

    public FakeGitHub(string directory)
    {
        _versionFile = Path.Combine(directory, "version.zip");
        using (var zip = ZipFile.Open(_versionFile, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("version.txt").Open()))
            writer.Write(OriginalVersion);
        _packagesFile = Path.Combine(directory, "packages.zip");
        using (var zip = ZipFile.Open(_packagesFile, ZipArchiveMode.Create))
        {
            foreach (var id in new[] { "Velopack", "Velopack.Build", "vpk" })
            {
                var package = Path.Combine(directory, id + ".nupkg");
                WritePackage(package, id);
                zip.CreateEntryFromFile(package, "../../untrusted/" + id + ".nupkg");
            }
            var plugin = Path.Combine(directory, "Example.Plugin.nupkg");
            WritePackage(plugin, "Example.Plugin", "2.0.0", "Velopack");
            zip.CreateEntryFromFile(plugin, "Example.Plugin.nupkg");
        }
        _version = ArtifactFor(1, "build-version", _versionFile);
        _packages = ArtifactFor(2, "packages", _packagesFile);
        _olderPackagesFile = Path.Combine(directory, "older-packages.zip");
        File.Copy(_packagesFile, _olderPackagesFile);
        using (var zip = ZipFile.Open(_olderPackagesFile, ZipArchiveMode.Update))
            zip.GetEntry("Example.Plugin.nupkg")!.Delete();
        _olderPackages = ArtifactFor(3, "packages", _olderPackagesFile);
    }

    private static Artifact ArtifactFor(long id, string name, string file) =>
        new(id, name, false, "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant());

    public static void WritePackage(string path, string id, string version = OriginalVersion, string dependency = "Newtonsoft.Json")
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open()))
        {
            writer.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata><id>{id}</id><version>{version}</version><authors>test</authors>
                        <description>Test package</description>
                        {(id == "vpk" ? "<packageTypes><packageType name=\"DotnetTool\" /></packageTypes>" : "")}
                        <dependencies><group targetFramework="net8.0"><dependency id="{dependency}" version="[13.0.4,)" /></group></dependencies>
                      </metadata>
                    </package>
                    """);
        }
        var payload = zip.CreateEntry("runtimes/linux-x64/native/test");
        payload.ExternalAttributes = 0x1ED << 16;
        using var stream = payload.Open();
        stream.Write(Encoding.UTF8.GetBytes("unchanged-payload"));
    }

    public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken)
    {
        OpenPullRequestQueries++;
        if (FailDiscovery) throw new IOException("GitHub unavailable");
        return Task.FromResult<IReadOnlyList<PullRequestInfo>>(OpenPullRequests);
    }

    public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken)
    {
        RunQueries++;
        if (FailDiscovery) throw new IOException("GitHub unavailable");
        return Task.FromResult<IReadOnlyList<WorkflowRun>>(Runs ?? [
            new(100, 1, "head", "completed", "success", "pull_request", [new(755)]),
                new(99, 1, "old", "completed", "success", "pull_request", [new(755)]),
                new(101, 1, "other", "completed", "success", "pull_request", [new(756)]),
                new(102, 1, "failed", "completed", "failure", "pull_request", [new(755)])
        ]);
    }

    public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken)
    {
        ArtifactQueries++;
        var packages = runId == 99 ? _olderPackages : _packages;
        return Task.FromResult<IReadOnlyList<Artifact>>([_version with { Expired = Expired }, packages with { Expired = Expired }]);
    }

    public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
    {
        if (FailDownloads) throw new IOException("Downloads unavailable");
        if (artifact.Id == _version.Id) VersionDownloads++;
        else PackageDownloads++;
        File.Copy(artifact.Id == _version.Id ? _versionFile
            : artifact.Id == _olderPackages.Id ? _olderPackagesFile : _packagesFile, destination);
        if (CorruptDownload && artifact.Id == _packages.Id) File.AppendAllText(destination, "corrupt");
        return Task.CompletedTask;
    }
}
