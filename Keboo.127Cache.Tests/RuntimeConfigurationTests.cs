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
using Microsoft.Extensions.Caching.Memory;
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
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProductionHostUsesLegacyAndIndexedEnvironmentFieldsTogether(bool legacyJson)
    {
        Directory.CreateDirectory(_directory);
        var user = Path.Combine(_directory, "user.json");
        var explicitFile = Path.Combine(_directory, "explicit.json");
        File.WriteAllText(user, legacyJson
            ? """
                { "Proxy": { "Repository": "user/old", "Workflow": "user.yml",
                  "PackagesArtifact": "user-packages", "VersionArtifact": null } }
                """
            : "{}");
        File.WriteAllText(explicitFile, """{ "Server": {} }""");
        using var environment = new ConfigurationEnvironment(new Dictionary<string, string?>
        {
            ["Proxy__Repository"] = "env/new",
            ["Sources__0__Artifact"] = "env-packages",
            ["Server__CacheDirectory"] = Path.Combine(_directory, "cache")
        });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = _directory,
            EnvironmentName = "Production"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        ConfigurationFiles.AddUserAndExplicitConfiguration(builder.Configuration, new FileInfo(explicitFile), user);
        builder.Services.AddProxy(builder.Configuration);
        var github = new Mock<IGitHubClient>(MockBehavior.Strict);
        github.Setup(client => client.GetOpenPullRequestsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PullRequestInfo>());
        builder.Services.AddSingleton(github.Object);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(CancellationToken.None);

        using var client = Client(app);
        using var health = JsonDocument.Parse(await client.GetStringAsync("/health", CancellationToken.None));
        var options = app.Services.GetRequiredService<ProxyOptions>();
        await Assert.That(health.RootElement.GetProperty("repository").GetString()).IsEqualTo("env/new");
        await Assert.That(options.Repository).IsEqualTo("env/new");
        await Assert.That(options.Workflow).IsEqualTo(legacyJson ? "user.yml" : "build.yml");
        await Assert.That(options.PackagesArtifact).IsEqualTo("env-packages");
        if (legacyJson) await Assert.That(options.VersionArtifact).IsNull();
        else await Assert.That(options.VersionArtifact).IsEqualTo("build-version");
        await Assert.That(github.Invocations.Count).IsEqualTo(1);
        await app.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SourceWithoutVersionArtifactServesPackagesWithoutInventingAnInstallerVersion()
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
        await Assert.That(builds.RootElement.GetArrayLength()).IsEqualTo(0);
        await Assert.That(github.Downloads).IsEqualTo(1);

        using var versions = JsonDocument.Parse(await client.GetStringAsync(
            "/flat/example.package/index.json", CancellationToken.None));
        var version = versions.RootElement.GetProperty("versions")[0].GetString()!;
        await Assert.That(version).IsEqualTo("1.2.3-pr.42.run.r0000000000000000100.attempt.1.artifact.7");
        using var otherVersions = JsonDocument.Parse(await client.GetStringAsync(
            "/flat/z.other/index.json", CancellationToken.None));
        await Assert.That(otherVersions.RootElement.GetProperty("versions")[0].GetString())
            .IsEqualTo("2.0.0-pr.42.run.r0000000000000000100.attempt.1.artifact.7");
        var (exitCode, output, commands) = await RunVpkInstallerAsync(baseUrl);
        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(output).Contains("No matching build");
        await Assert.That(commands).IsEmpty();
        await Assert.That(Directory.Exists(Path.Combine(_directory, "installer-work", "state"))).IsFalse();
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
                [new RunArtifacts(99, 1, 42, "old", github.Packages, null, WorkflowVerified: true)], SchemaVersion: 2), CancellationToken.None);
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
    public async Task CachedOpenPrBuildRemainsVisibleOutsideTheDiscoveryWindow()
    {
        var github = new PackageGitHub(_directory)
        {
            OpenPullRequests = [new(42)],
            Runs = [new(100, 1, "current", "completed", "success", "pull_request", [new(42)])]
        };
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile("owner/repo", "ci.yml",
            [
                new RunArtifacts(90, 1, 42, "previously-pinned", github.Packages, null, WorkflowVerified: true),
                new RunArtifacts(999, 1, 43, "closed", github.Packages, null, WorkflowVerified: true)
            ], SchemaVersion: 2), CancellationToken.None);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:Repository"] = "owner/repo",
            ["Proxy:Workflow"] = "ci.yml",
            ["Proxy:PackagesArtifact"] = "packages",
            ["Proxy:VersionArtifact"] = null,
            ["Proxy:CacheDirectory"] = cache,
            ["Proxy:RunIds:0"] = "90"
        }).Build();
        var services = new ServiceCollection().AddProxy(configuration);
        services.AddSingleton<IGitHubClient>(github);
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<PackageCatalog>();
        await Assert.That(provider.GetRequiredService<ProxyOptions>().RunIds).IsEmpty();

        var initial = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(initial.Any(x => x.Id == "Example.Package" && x.Build.RunId == 100)).IsTrue();
        await Assert.That(github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(github.Downloads).IsEqualTo(1);

        github.Runs = Enumerable.Range(0, 100)
            .Select(i => new WorkflowRun(1000 + i, 1, "closed", "completed", "success",
                "pull_request", [new(43)]))
            .ToArray();
        ((MemoryCache)provider.GetRequiredService<IMemoryCache>()).Compact(1);
        var outsideWindow = await catalog.GetRunArtifactsAsync(CancellationToken.None);
        await Assert.That(outsideWindow.Select(x => x.RunId).ToArray()).IsEquivalentTo(new long[] { 100 });
        var builds = await catalog.GetBuildsAsync(CancellationToken.None);
        await Assert.That(builds.Select(x => x.RunId).ToArray()).IsEquivalentTo(new long[] { 100 });
        var packages = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(packages.Any(x => x.Id == "Example.Package" && x.Build.RunId == 100)).IsTrue();
        await Assert.That(github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(github.Downloads).IsEqualTo(1);

        using (var restarted = services.BuildServiceProvider())
        {
            var restored = await restarted.GetRequiredService<PackageCatalog>().GetRunArtifactsAsync(CancellationToken.None);
            await Assert.That(restored.Select(x => x.RunId).ToArray()).IsEquivalentTo(new long[] { 100 });
        }

        github.Runs = [new(100, 2, "rerun", "completed", "success", "pull_request", [new(42)])];
        ((MemoryCache)provider.GetRequiredService<IMemoryCache>()).Compact(1);
        var rerun = await catalog.GetRunArtifactsAsync(CancellationToken.None);
        await Assert.That(rerun.Single().RunId).IsEqualTo(100L);
        await Assert.That(rerun.Single().Attempt).IsEqualTo(2);

        github.Runs = [new(101, 1, "newer", "completed", "success", "pull_request", [new(42)])];
        ((MemoryCache)provider.GetRequiredService<IMemoryCache>()).Compact(1);
        var newer = await catalog.GetRunArtifactsAsync(CancellationToken.None);
        await Assert.That(newer.Select(x => x.RunId).ToArray()).IsEquivalentTo(new long[] { 101 });

        github.OpenPullRequests = [];
        ((MemoryCache)provider.GetRequiredService<IMemoryCache>()).Compact(1);
        await Assert.That(await catalog.GetRunArtifactsAsync(CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task CachedFallbackStillRejectsADifferentConfiguredArtifact()
    {
        var github = new PackageGitHub(_directory)
        {
            OpenPullRequests = [new(42)],
            Runs = [new(1000, 1, "closed", "completed", "success", "pull_request", [new(43)])]
        };
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(cache);
        var runsFile = Path.Combine(cache, "runs.json");
        await JsonSettings.WriteAtomicAsync(runsFile,
            new RunArtifactsFile("owner/repo", "ci.yml",
                [new RunArtifacts(100, 1, 42, "cached", github.Packages with { Name = "old" }, null, WorkflowVerified: true)], SchemaVersion: 2),
            CancellationToken.None);
        var original = await File.ReadAllTextAsync(runsFile);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = cache,
            ["Sources:0:Name"] = "example",
            ["Sources:0:Repository"] = "owner/repo",
            ["Sources:0:Workflow"] = "ci.yml",
            ["Sources:0:Artifact"] = "packages"
        }).Build();
        var services = new ServiceCollection().AddProxy(configuration);
        services.AddSingleton<IGitHubClient>(github);
        using var provider = services.BuildServiceProvider();

        InvalidDataException? error = null;
        try { await provider.GetRequiredService<PackageCatalog>().GetRunArtifactsAsync(CancellationToken.None); }
        catch (InvalidDataException caught) { error = caught; }

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message).Contains("Server.CacheDirectory");
        await Assert.That(await File.ReadAllTextAsync(runsFile)).IsEqualTo(original);
        await Assert.That(github.ArtifactQueries).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CachedOpenPrBuildRemainsVisibleWhenNewerArtifactsAreUnavailable(bool expired)
    {
        var github = new PackageGitHub(_directory)
        {
            OpenPullRequests = [new(42)],
            Runs = [new(100, 1, "cached", "completed", "success", "pull_request", [new(42)])]
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = Path.Combine(_directory, "cache"),
            ["Sources:0:Name"] = "example",
            ["Sources:0:Repository"] = "owner/repo",
            ["Sources:0:Workflow"] = "ci.yml",
            ["Sources:0:Artifact"] = "packages"
        }).Build();
        var services = new ServiceCollection().AddProxy(configuration);
        services.AddSingleton<IGitHubClient>(github);
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<PackageCatalog>();
        var memoryCache = (MemoryCache)provider.GetRequiredService<IMemoryCache>();

        var original = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(original.Any(x => x.Id == "Example.Package" && x.Build.RunId == 100)).IsTrue();

        github.Runs = [new(101, 1, "newer", "completed", "success", "pull_request", [new(42)])];
        github.UnavailableArtifactRun = 101;
        github.ExpiredUnavailableArtifact = expired;
        memoryCache.Compact(1);
        var fallback = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(fallback.Any(x => x.Id == "Example.Package" && x.Build.RunId == 100)).IsTrue();

        github.UnavailableArtifactRun = null;
        memoryCache.Compact(1);
        var recovered = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(recovered.Any(x => x.Id == "Example.Package" && x.Build.RunId == 101)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExistingInstallerSelectsTheVpkVersionRatherThanAnUnrelatedPackage(bool previouslyGuessed)
    {
        var github = new PackageGitHub(_directory, includeVpk: true) { OpenPullRequests = [new(42)] };
        var cache = Path.Combine(_directory, "cache");
        if (previouslyGuessed)
        {
            Directory.CreateDirectory(cache);
            await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
                new RunArtifactsFile("owner/repo", "ci.yml",
                    [new RunArtifacts(100, 1, 42, "current", github.Packages, null, WorkflowVerified: true)], SchemaVersion: 2), CancellationToken.None);
            await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
                new CatalogFile("owner/repo",
                    [new BuildEntry(100, 1, 42, "current", "1.2.3",
                        "1.2.3-pr.42.run.r0000000000000000100.attempt.1.artifact.7", github.Packages, null)]), CancellationToken.None);
        }
        var baseUrl = FreeUrl();
        var builder = CreateBuilder(cache, new Dictionary<string, string?>
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

        var (exitCode, output, commands) = await RunVpkInstallerAsync(baseUrl);
        if (exitCode != 0) throw new InvalidOperationException($"The existing installer failed:\n{output}");
        using var client = Client(app);
        using var builds = JsonDocument.Parse(await client.GetStringAsync("/builds", CancellationToken.None));
        var build = builds.RootElement[0];
        var version = build.GetProperty("version").GetString();
        await Assert.That(build.GetProperty("originalVersion").GetString()).IsEqualTo("3.4.5");
        await Assert.That(version).IsEqualTo("3.4.5-pr.42.run.r0000000000000000100.attempt.1.artifact.7");
        await Assert.That(commands).Contains($"tool install vpk --version {version}");
        using var vpkVersions = JsonDocument.Parse(await client.GetStringAsync("/flat/vpk/index.json", CancellationToken.None));
        await Assert.That(vpkVersions.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(version);
        var manifest = await client.GetStringAsync($"/flat/vpk/{version}/vpk.nuspec", CancellationToken.None);
        await Assert.That(manifest).Contains(version!);
        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(Path.Combine(cache, "catalog.json")),
            JsonSettings.Options)!;
        await Assert.That(saved.Builds.Single().Version).IsEqualTo("");
        await Assert.That(saved.Builds.Single().OriginalVersion).IsEqualTo("");
        await app.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task VersionArtifactRetainsLegacyBuildVersionWithoutDownloadingPackages()
    {
        var github = new PackageGitHub(_directory, includeVersionArtifact: true) { OpenPullRequests = [new(42)] };
        var baseUrl = FreeUrl();
        var builder = CreateBuilder(Path.Combine(_directory, "cache"), new Dictionary<string, string?>
        {
            ["Server:BaseUrl"] = baseUrl,
            ["Sources:0:Name"] = "example",
            ["Sources:0:Repository"] = "owner/repo",
            ["Sources:0:Workflow"] = "ci.yml",
            ["Sources:0:Artifact"] = "packages",
            ["Sources:0:VersionArtifact"] = "build-version"
        }, baseUrl);
        builder.Services.AddSingleton<IGitHubClient>(github);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(CancellationToken.None);

        using var client = Client(app);
        using var builds = JsonDocument.Parse(await client.GetStringAsync("/builds", CancellationToken.None));
        await Assert.That(builds.RootElement.GetArrayLength()).IsEqualTo(1);
        await Assert.That(builds.RootElement[0].GetProperty("originalVersion").GetString()).IsEqualTo("9.0.0");
        await Assert.That(builds.RootElement[0].GetProperty("version").GetString())
            .IsEqualTo("9.0.0-pr.42.run.r0000000000000000100.attempt.1.artifact.7");
        await Assert.That(builds.RootElement[0].GetProperty("versionArtifact").GetProperty("id").GetInt64()).IsEqualTo(8L);
        await Assert.That(github.VersionDownloads).IsEqualTo(1);
        await Assert.That(github.Downloads).IsEqualTo(1);
        await app.StopAsync(CancellationToken.None);
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
                [new RunArtifacts(100, 1, 42, "current", github.Packages, versionArtifact, WorkflowVerified: true)], SchemaVersion: 2), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
            new CatalogFile("owner/repo",
                [new BuildEntry(100, 1, 42, "current", "9.0.0",
                    "9.0.0-pr.42.run.r0000000000000000100.attempt.1.artifact.7", github.Packages, versionArtifact)],
                CacheSchema.Version), CancellationToken.None);
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

        var catalog = provider.GetRequiredService<PackageCatalog>();
        var builds = await catalog.GetBuildsAsync(CancellationToken.None);

        await Assert.That(builds.Length).IsEqualTo(1);
        await Assert.That(builds[0].OriginalVersion).IsEqualTo("");
        await Assert.That(builds[0].Version).IsEqualTo("");
        await Assert.That(builds[0].VersionArtifact).IsNull();
        await Assert.That(github.ArtifactQueries).IsEqualTo(0);
        await Assert.That(github.Downloads).IsEqualTo(1);
        var packages = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(packages.Single(x => x.Id == "Example.Package").Build.OriginalVersion).IsEqualTo("1.2.3");
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
                [new RunArtifacts(100, 1, 42, "current", github.Packages, null, WorkflowVerified: true)], SchemaVersion: 2), CancellationToken.None);
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

    private async Task<(int ExitCode, string Output, string Commands)> RunVpkInstallerAsync(string baseUrl)
    {
        var bin = Path.Combine(_directory, "fake-bin");
        var work = Path.Combine(_directory, "installer-work");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(work);
        var log = Path.Combine(work, "dotnet.log");
        var fakeDotnet = Path.Combine(bin, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet");
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(fakeDotnet, "@echo off\r\necho %*>>\"%FAKE_DOTNET_LOG%\"\r\nexit /b 0\r\n");
        else
        {
            await File.WriteAllTextAsync(fakeDotnet, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$FAKE_DOTNET_LOG\"\n");
            File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "scripts", "Install-PrBuild.ps1"));
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
            "-ProxyUrl", baseUrl, "-PullRequest", "42", "-ToolPath", Path.Combine(work, "tools"),
            "-StateDirectory", Path.Combine(work, "state"), "-NuGetConfig", Path.Combine(work, "nuget.config") })
            start.ArgumentList.Add(argument);
        start.Environment["PATH"] = bin + Path.PathSeparator + start.Environment["PATH"];
        start.Environment["FAKE_DOTNET_LOG"] = log;
        start.Environment["DOTNET_CLI_HOME"] = work;
        // Without this, the SDK appends <DOTNET_CLI_HOME>\.dotnet\tools to the real user PATH.
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["TEMP"] = work;
        start.Environment["TMP"] = work;
        start.Environment["TMPDIR"] = work;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        return (process.ExitCode, await stdout + await stderr,
            File.Exists(log) ? await File.ReadAllTextAsync(log) : "");
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
        // Without this, the SDK appends <DOTNET_CLI_HOME>\.dotnet\tools to the real user PATH.
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
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
        private readonly string? _versionZip;
        private readonly Artifact? _versionArtifact;
        public Artifact Packages => _artifact;
        public PullRequestInfo[] OpenPullRequests { get; set; } = [];
        public WorkflowRun[]? Runs { get; set; }
        public long? UnavailableArtifactRun { get; set; }
        public bool ExpiredUnavailableArtifact { get; set; }
        public int ArtifactQueries { get; private set; }
        public int Downloads { get; private set; }
        public int VersionDownloads { get; private set; }

        public PackageGitHub(string directory, bool includeVpk = false, bool includeVersionArtifact = false)
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
                if (includeVpk)
                {
                    var vpk = Path.Combine(directory, "vpk.nupkg");
                    using (var vpkZip = ZipFile.Open(vpk, ZipArchiveMode.Create))
                    using (var writer = new StreamWriter(vpkZip.CreateEntry("VPK.nuspec").Open(), Encoding.UTF8))
                        writer.Write("""
                            <?xml version="1.0" encoding="utf-8"?>
                            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                              <metadata><id>VPK</id><version>3.4.5</version>
                                <authors>test</authors><description>Test tool</description></metadata>
                            </package>
                            """);
                    zip.CreateEntryFromFile(vpk, "vpk.nupkg");
                }
            }
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_zip))).ToLowerInvariant();
            _artifact = new Artifact(7, "packages", false, "sha256:" + digest);
            if (includeVersionArtifact)
            {
                _versionZip = Path.Combine(directory, "version.zip");
                using (var zip = ZipFile.Open(_versionZip, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("version.txt").Open(), Encoding.UTF8))
                    writer.Write("9.0.0");
                var versionDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_versionZip))).ToLowerInvariant();
                _versionArtifact = new Artifact(8, "build-version", false, "sha256:" + versionDigest);
            }
        }

        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PullRequestInfo>>(OpenPullRequests);

        public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>(Runs ?? [
                new(100, 1, "current", "completed", "success", "pull_request", [new(42)]),
                new(99, 1, "old", "completed", "success", "pull_request", [new(42)]),
                new(101, 1, "other", "completed", "success", "pull_request", [new(43)]),
                new(102, 1, "failed", "completed", "failure", "pull_request", [new(42)])
            ]);

        public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken)
        {
            ArtifactQueries++;
            if (runId == UnavailableArtifactRun)
                return Task.FromResult<IReadOnlyList<Artifact>>(ExpiredUnavailableArtifact
                    ? [_artifact with { Id = _artifact.Id + 1, Expired = true }] : []);
            return Task.FromResult<IReadOnlyList<Artifact>>(_versionArtifact is null
                ? [_artifact] : [_artifact, _versionArtifact]);
        }

        public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
        {
            if (artifact.Id == _artifact.Id)
            {
                Downloads++;
                File.Copy(_zip, destination);
            }
            else if (artifact.Id == _versionArtifact?.Id)
            {
                VersionDownloads++;
                File.Copy(_versionZip!, destination);
            }
            else throw new InvalidOperationException("Unexpected artifact download.");
            return Task.CompletedTask;
        }
    }
}