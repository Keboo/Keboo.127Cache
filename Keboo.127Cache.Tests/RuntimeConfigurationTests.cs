using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;

namespace Keboo._127Cache.Tests;

public sealed class RuntimeConfigurationTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(
        "artifacts", "runtime-configuration-tests", Guid.NewGuid().ToString("N")));

    [Test]
    public async Task EmptySourcesStartAndServeAnEmptyFeedWithoutContactingGitHub()
    {
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "catalog.json"), """{ "repository": "other/repo", "builds": [] }""");
        File.WriteAllText(Path.Combine(cache, "runs.json"), """{ "repository": "other/repo", "workflow": "old.yml", "runs": [] }""");
        var github = new Mock<IGitHubClient>(MockBehavior.Strict);
        var builder = CreateBuilder(cache, new Dictionary<string, string?> { ["Sources"] = "" });
        builder.Services.AddSingleton(github.Object);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(CancellationToken.None);

        using var client = Client(app);
        using var builds = JsonDocument.Parse(await client.GetStringAsync("/builds", CancellationToken.None));
        using var search = JsonDocument.Parse(await client.GetStringAsync(
            "/query?prerelease=true&semVerLevel=2.0.0", CancellationToken.None));
        using var autocomplete = JsonDocument.Parse(await client.GetStringAsync(
            "/autocomplete?prerelease=true&semVerLevel=2.0.0", CancellationToken.None));
        var missing = await client.GetAsync("/flat/missing/index.json", CancellationToken.None);

        await Assert.That(builds.RootElement.GetArrayLength()).IsEqualTo(0);
        await Assert.That(search.RootElement.GetProperty("data").GetArrayLength()).IsEqualTo(0);
        await Assert.That(autocomplete.RootElement.GetProperty("data").GetArrayLength()).IsEqualTo(0);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(github.Invocations.Count).IsEqualTo(0);
        await app.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SourceWithoutVersionArtifactServesThePackageVersionFromItsNuspec()
    {
        var github = new PackageGitHub(_directory) { OpenPullRequests = [new(42)] };
        var baseUrl = FreeUrl();
        var builder = CreateBuilder(Path.Combine(_directory, "cache"), new Dictionary<string, string?>
        {
            ["Server:BaseUrl"] = baseUrl,
            ["Sources:0:Name"] = "example",
            ["Sources:0:Repository"] = "owner/repo",
            ["Sources:0:Workflow"] = "ci.yml",
            ["Sources:0:Artifact"] = "packages"
        }, baseUrl);
        builder.Services.AddSingleton<IGitHubClient>(github);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(CancellationToken.None);

        await Assert.That(github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(github.Downloads).IsEqualTo(0);
        using var client = Client(app);
        using var builds = JsonDocument.Parse(await client.GetStringAsync("/builds", CancellationToken.None));
        var build = builds.RootElement[0];
        var version = build.GetProperty("version").GetString()!;
        await Assert.That(build.GetProperty("runId").GetInt64()).IsEqualTo(100L);
        await Assert.That(build.GetProperty("originalVersion").GetString()).IsEqualTo("1.2.3");
        await Assert.That(build.GetProperty("versionArtifact").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(version).IsEqualTo("1.2.3-pr.42.run.100.attempt.1.artifact.7");
        await Assert.That(github.Downloads).IsEqualTo(1);

        using var versions = JsonDocument.Parse(await client.GetStringAsync(
            "/flat/example.package/index.json", CancellationToken.None));
        await Assert.That(versions.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(version);
        using var otherVersions = JsonDocument.Parse(await client.GetStringAsync(
            "/flat/z.other/index.json", CancellationToken.None));
        await Assert.That(otherVersions.RootElement.GetProperty("versions")[0].GetString())
            .IsEqualTo("2.0.0-pr.42.run.100.attempt.1.artifact.7");
        var bytes = await client.GetByteArrayAsync(
            $"/flat/example.package/{version}/example.package.{version}.nupkg", CancellationToken.None);
        using var reader = new PackageArchiveReader(new MemoryStream(bytes));
        await Assert.That(reader.GetIdentity().Id).IsEqualTo("Example.Package");
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
        await RestoreFromFeedAsync(baseUrl, version);
        await Assert.That(github.Downloads).IsEqualTo(1);
        await app.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task LegacyFiltersDoNotSelectPrsOrPinOldRuns()
    {
        var github = new PackageGitHub(_directory) { OpenPullRequests = [new(42), new(43)] };
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile("owner/repo", "ci.yml",
                [new RunArtifacts(99, 1, 42, "old", github.Packages, null)]), CancellationToken.None);
        var entries = new Dictionary<string, string?>
        {
            ["Proxy:Repository"] = "owner/repo",
            ["Proxy:Workflow"] = "ci.yml",
            ["Proxy:PackagesArtifact"] = "packages",
            ["Proxy:VersionArtifact"] = null,
            ["Proxy:CacheDirectory"] = cache,
            ["Proxy:PullRequests:0"] = "42",
            ["Proxy:RunIds:0"] = "99",
            ["Proxy:RunLimit"] = "1"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
        var services = new ServiceCollection().AddProxy(config);
        services.AddSingleton<IGitHubClient>(github);
        using var provider = services.BuildServiceProvider();

        var runs = await provider.GetRequiredService<PackageCatalog>()
            .GetRunArtifactsAsync(CancellationToken.None);

        await Assert.That(runs.Length).IsEqualTo(2);
        await Assert.That(runs.Any(x => x.PullRequest == 42 && x.RunId == 100)).IsTrue();
        await Assert.That(runs.Any(x => x.PullRequest == 43 && x.RunId == 101)).IsTrue();
        await Assert.That(runs.Any(x => x.RunId == 99)).IsFalse();
    }

    [Test]
    public async Task CachedLegacyVersionMetadataDoesNotMakeTheOptionalArtifactRequired()
    {
        var github = new PackageGitHub(_directory) { OpenPullRequests = [new(42)] };
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        var versionArtifact = new Artifact(8, "build-version", false, null);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile("owner/repo", "ci.yml",
                [new RunArtifacts(100, 1, 42, "current", github.Packages, versionArtifact)]), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
            new CatalogFile("owner/repo",
                [new BuildEntry(100, 1, 42, "current", "9.0.0",
                    "9.0.0-pr.42.run.100.attempt.1.artifact.7", github.Packages, versionArtifact)]), CancellationToken.None);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = cache,
            ["Sources:0:Name"] = "example",
            ["Sources:0:Repository"] = "owner/repo",
            ["Sources:0:Workflow"] = "ci.yml",
            ["Sources:0:Artifact"] = "packages"
        }).Build();
        var services = new ServiceCollection().AddProxy(config);
        services.AddSingleton<IGitHubClient>(github);
        using var provider = services.BuildServiceProvider();

        var builds = await provider.GetRequiredService<PackageCatalog>().GetBuildsAsync(CancellationToken.None);

        await Assert.That(builds.Length).IsEqualTo(1);
        await Assert.That(builds[0].OriginalVersion).IsEqualTo("1.2.3");
        await Assert.That(builds[0].VersionArtifact).IsNull();
        await Assert.That(github.ArtifactQueries).IsEqualTo(0);
        await Assert.That(github.Downloads).IsEqualTo(1);
    }

    [Test]
    public async Task ChangingArtifactDoesNotReplaceImmutableCachedRunMetadata()
    {
        var github = new PackageGitHub(_directory) { OpenPullRequests = [new(42)] };
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        var runsFile = Path.Combine(cache, "runs.json");
        await JsonSettings.WriteAtomicAsync(runsFile,
            new RunArtifactsFile("owner/repo", "ci.yml",
                [new RunArtifacts(100, 1, 42, "current", github.Packages, null)]), CancellationToken.None);
        var original = await File.ReadAllTextAsync(runsFile);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = cache,
            ["Sources:0:Name"] = "example",
            ["Sources:0:Repository"] = "owner/repo",
            ["Sources:0:Workflow"] = "ci.yml",
            ["Sources:0:Artifact"] = "different"
        }).Build();
        var services = new ServiceCollection().AddProxy(config);
        services.AddSingleton<IGitHubClient>(github);
        using var provider = services.BuildServiceProvider();

        InvalidDataException? error = null;
        try { await provider.GetRequiredService<PackageCatalog>().GetRunArtifactsAsync(CancellationToken.None); }
        catch (InvalidDataException caught) { error = caught; }

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message).Contains("Server.CacheDirectory");
        await Assert.That(await File.ReadAllTextAsync(runsFile)).IsEqualTo(original);
        await Assert.That(github.ArtifactQueries).IsEqualTo(0);
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    private WebApplicationBuilder CreateBuilder(
        string cacheDirectory, Dictionary<string, string?> entries, string? listenUrl = null)
    {
        Directory.CreateDirectory(_directory);
        entries["Server:CacheDirectory"] = cacheDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _directory,
            EnvironmentName = "Production"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(listenUrl ?? "http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(entries);
        builder.Services.AddProxy(builder.Configuration);
        return builder;
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private async Task RestoreFromFeedAsync(string baseUrl, string version)
    {
        var project = Path.Combine(_directory, "consumer.csproj");
        var nugetConfig = Path.Combine(_directory, "NuGet.config");
        var packages = Path.Combine(_directory, "nuget-packages");
        var work = Path.Combine(_directory, "work");
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Package" Version="{{version}}" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(nugetConfig, $$"""
            <configuration><packageSources><clear />
              <add key="local" value="{{baseUrl}}/v3/index.json" allowInsecureConnections="true" />
            </packageSources></configuration>
            """);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "restore", project, "--configfile", nugetConfig,
            "--packages", packages, "--disable-parallel", "--verbosity", "quiet" })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_HOME"] = work;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(work, "http-cache");
        start.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(work, "plugins-cache");
        start.Environment["TEMP"] = work;
        start.Environment["TMP"] = work;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet restore.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var output = await stdout + await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet restore failed:\n{output}");

        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "obj", "project.assets.json")));
        await Assert.That(assets.RootElement.GetProperty("libraries").TryGetProperty($"Example.Package/{version}", out _)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(packages, "example.package", version, $"example.package.{version}.nupkg"))).IsTrue();
    }

    private static string FreeUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class PackageGitHub : IGitHubClient
    {
        private readonly string _zip;
        private readonly Artifact _artifact;
        public Artifact Packages => _artifact;
        public PullRequestInfo[] OpenPullRequests { get; set; } = [];
        public int ArtifactQueries { get; private set; }
        public int Downloads { get; private set; }

        public PackageGitHub(string directory)
        {
            Directory.CreateDirectory(directory);
            var package = Path.Combine(directory, "package.nupkg");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Example.Package.nuspec").Open(), Encoding.UTF8))
                writer.Write("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata><id>Example.Package</id><version>1.2.3</version>
                        <authors>test</authors><description>Test package</description></metadata>
                    </package>
                    """);
            var otherPackage = Path.Combine(directory, "other.nupkg");
            using (var zip = ZipFile.Open(otherPackage, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Z.Other.nuspec").Open(), Encoding.UTF8))
                writer.Write("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata><id>Z.Other</id><version>2.0.0</version>
                        <authors>test</authors><description>Test package</description></metadata>
                    </package>
                    """);
            _zip = Path.Combine(directory, "packages.zip");
            using (var zip = ZipFile.Open(_zip, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(package, "Example.Package.nupkg");
                zip.CreateEntryFromFile(otherPackage, "Z.Other.nupkg");
            }
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_zip))).ToLowerInvariant();
            _artifact = new Artifact(7, "packages", false, "sha256:" + digest);
        }

        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PullRequestInfo>>(OpenPullRequests);

        public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>([
                new(100, 1, "current", "completed", "success", "pull_request", [new(42)]),
                new(99, 1, "old", "completed", "success", "pull_request", [new(42)]),
                new(101, 1, "other", "completed", "success", "pull_request", [new(43)]),
                new(102, 1, "failed", "completed", "failure", "pull_request", [new(42)])
            ]);

        public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken)
        {
            ArtifactQueries++;
            return Task.FromResult<IReadOnlyList<Artifact>>([_artifact]);
        }

        public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
        {
            if (artifact.Id != _artifact.Id) throw new InvalidOperationException("Unexpected artifact download.");
            Downloads++;
            File.Copy(_zip, destination);
            return Task.CompletedTask;
        }
    }
}
