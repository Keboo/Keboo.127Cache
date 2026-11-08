using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache.Tests;

public class NuGetLongVersionRestoreTests
{
    [Test]
    public async Task RealNuGetRestoreSucceedsAtTheSafeLongFilenameBoundary()
    {
        using var fixture = new PackageRewriteFixture();
        var original = "1.2.0-preview." + new string('a', 163);
        var github = await fixture.CreateGitHubAsync(coreVersion: original);
        var version = PackageRewriter.VersionFor(original, 755, 100, 1, 200);
        await Assert.That(NuGetVersion.TryParse(version, out _)).IsTrue();
        await Assert.That($"acme.core.{version}.nupkg.sha512".Length).IsEqualTo(255);
        await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>());

        var result = await RestoreAsync(fixture.DirectoryPath, app.Urls.Single() + "/v3/index.json", "Acme.Core", version);

        if (result.ExitCode != 0) throw new InvalidOperationException($"Real dotnet restore failed:\n{result.Output}");
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.ProjectDirectory, "obj", "project.assets.json")));
        await Assert.That(assets.RootElement.GetProperty("libraries").TryGetProperty($"Acme.Core/{version}", out _)).IsTrue();
        var packageDirectory = Path.Combine(result.ProjectDirectory, "packages", "acme.core", version);
        await Assert.That(File.Exists(Path.Combine(packageDirectory, $"acme.core.{version}.nupkg.sha512"))).IsTrue();
        using var package = new PackageArchiveReader(Path.Combine(packageDirectory, $"acme.core.{version}.nupkg"));
        await Assert.That(package.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
    }

    [Test]
    public async Task RealNuGetRestoreExposesAnOverlongClientCacheComponent()
    {
        using var fixture = new PackageRewriteFixture();
        var original = "1.2.0-preview." + new string('a', 164);
        var version = PackageRewriter.VersionFor(original, 755, 100, 1, 200);
        await Assert.That(NuGetVersion.TryParse(version, out _)).IsTrue();
        await Assert.That($"acme.core.{version}.nupkg.sha512".Length).IsEqualTo(256);
        var package = fixture.CreatePackage("Acme.Core", original);
        var rewritten = Path.Combine(fixture.DirectoryPath, "reference.nupkg");
        await PackageRewriter.RewriteAsync(package, rewritten, "Acme.Core", original, version);
        var counter = new DownloadCounter();
        await using var app = await StartReferenceFeedAsync(fixture, version, rewritten, counter);

        var result = await RestoreAsync(fixture.DirectoryPath, app.Urls.Single() + "/v3/index.json", "Acme.Core", version);

        await Assert.That(counter.Packages > 0).IsTrue();
        if (Has255ByteComponentLimit(fixture.DirectoryPath))
        {
            await Assert.That(result.ExitCode != 0).IsTrue();
            await Assert.That(result.Output.Contains("too long", StringComparison.OrdinalIgnoreCase)
                || result.Output.Contains("filename, directory name, or volume label syntax is incorrect", StringComparison.OrdinalIgnoreCase)).IsTrue();
        }
        else
        {
            if (result.ExitCode != 0) throw new InvalidOperationException($"Real dotnet restore failed:\n{result.Output}");
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.ProjectDirectory, "obj", "project.assets.json")));
            await Assert.That(assets.RootElement.GetProperty("libraries").TryGetProperty($"Acme.Core/{version}", out _)).IsTrue();
        }
    }

    [Test]
    public async Task OverlongBuildIsIsolatedAndAHealthyBuildStillRestores()
    {
        using var fixture = new PackageRewriteFixture();
        var original = "1.2.0-preview." + new string('a', 164);
        var github = await fixture.CreateGitHubAsync(coreVersion: original);
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        github.SeedRawCache(cache);
        github.FailDownloads = true;
        var safePackage = fixture.CreatePackage("Safe.Package", "1.0.0");
        var safeArtifactPath = Path.Combine(cache, "artifacts", "300.zip");
        using (var archive = ZipFile.Open(safeArtifactPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(safePackage, "Safe.Package.nupkg");
        var safeArtifact = new Artifact(300, "packages", true,
            "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(safeArtifactPath))).ToLowerInvariant());
        var safeVersion = PackageRewriter.VersionFor("1.0.0", 755, 99, 1, 300);
        var longVersion = PackageRewriter.VersionFor(original, 755, 100, 1, 200);
        var safe = new BuildEntry(99, 1, 755, "safe", "1.0.0", safeVersion, safeArtifact, github.Version);
        var overlong = new BuildEntry(100, 1, 755, "long", original, longVersion, github.Packages, github.Version);
        var builds = new[] { safe, overlong };
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = cache, PullRequests = [755] };
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, builds.Select(x =>
                new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact)).ToList()),
            CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
            new CatalogFile(options.Repository, builds.ToList(), CacheSchema.Version), CancellationToken.None);

        var logger = new CollectingLogger<PackageCatalog>();
        await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
            new CollectingLogger<PackageCache>(), catalogLogger: logger);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        using var missing = await client.GetAsync("flat/acme.core/index.json");
        await Assert.That((int)missing.StatusCode).IsEqualTo(404);
        using var index = JsonDocument.Parse(await client.GetStringAsync("flat/safe.package/index.json"));
        await Assert.That(index.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(safeVersion);
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("100", StringComparison.Ordinal)
            && x.Message.Contains("200", StringComparison.Ordinal)
            && x.Exception?.Message.Contains("NuGet client cache filename", StringComparison.Ordinal) == true
            && (x.Exception.Message.Contains("Windows component limit", StringComparison.Ordinal)
                || x.Exception.Message.Contains("Unix component limit", StringComparison.Ordinal)))).IsTrue();

        var healthyRestore = await RestoreAsync(fixture.DirectoryPath, app.Urls.Single() + "/v3/index.json", "Safe.Package", safeVersion);
        if (healthyRestore.ExitCode != 0) throw new InvalidOperationException($"Healthy dotnet restore failed:\n{healthyRestore.Output}");
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(healthyRestore.ProjectDirectory, "obj", "project.assets.json")));
        await Assert.That(assets.RootElement.GetProperty("libraries").TryGetProperty($"Safe.Package/{safeVersion}", out _)).IsTrue();
        var blockedRestore = await RestoreAsync(fixture.DirectoryPath, app.Urls.Single() + "/v3/index.json", "Acme.Core", longVersion);
        await Assert.That(blockedRestore.ExitCode != 0).IsTrue();
        await Assert.That(blockedRestore.Output.Contains("NU1101", StringComparison.Ordinal)
            || blockedRestore.Output.Contains("NU1102", StringComparison.Ordinal)).IsTrue();
        await Assert.That(blockedRestore.Output.Contains("Acme.Core", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(cache, "artifacts", "200.zip"))).IsTrue();
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task UnicodePackageIdRestoreIsWindowsSafeAndUnixGuarded()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = cache, PullRequests = [755] };
        github.SeedRawCache(cache);
        github.FailDownloads = true;

        var id = new string('界', 90);
        var original = "1.2.0";
        var version = PackageRewriter.VersionFor(original, 755, 100, 1, 300);
        var package = Path.Combine(fixture.DirectoryPath, "unicode.nupkg");
        CreateMinimalPackage(package, id, original);
        var artifactPath = Path.Combine(cache, "artifacts", "300.zip");
        using (var archive = ZipFile.Open(artifactPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(package, $"{id}.nupkg");
        var artifact = new Artifact(300, "packages", true,
            "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath))).ToLowerInvariant());
        var build = new BuildEntry(100, 1, 755, "unicode", original, version, artifact, github.Version);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow,
                [new RunArtifacts(build.RunId, build.Attempt, build.PullRequest, build.HeadSha, build.Packages, build.VersionArtifact)]),
            CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
            new CatalogFile(options.Repository, [build], CacheSchema.Version), CancellationToken.None);

        var logger = new CollectingLogger<PackageCatalog>();
        await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
            new CollectingLogger<PackageCache>(), catalogLogger: logger);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var idPath = Uri.EscapeDataString(id.ToLowerInvariant());

        if (OperatingSystem.IsWindows())
        {
            using var index = JsonDocument.Parse(await client.GetStringAsync($"flat/{idPath}/index.json"));
            await Assert.That(index.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(version);
            var restore = await RestoreAsync(fixture.DirectoryPath, app.Urls.Single() + "/v3/index.json", id, version);
            if (restore.ExitCode != 0) throw new InvalidOperationException($"Unicode restore failed:\n{restore.Output}");
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(restore.ProjectDirectory, "obj", "project.assets.json")));
            await Assert.That(assets.RootElement.GetProperty("libraries").TryGetProperty($"{id}/{version}", out _)).IsTrue();
        }
        else
        {
            using var response = await client.GetAsync($"flat/{idPath}/index.json");
            await Assert.That((int)response.StatusCode).IsEqualTo(404);
            await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
                && x.Exception?.Message.Contains("255-byte Unix component limit", StringComparison.Ordinal) == true)).IsTrue();
        }
    }

    [Test]
    public async Task RealNuGetFloatingRestoreSelectsTheNewestLargeRunWithRunTokens()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        github.Runs =
        [
            new(3_000_000_000, 1, "old", "completed", "success", "pull_request", [new(755)]),
            new(10_000_000_000, 1, "new", "completed", "success", "pull_request", [new(755)])
        ];
        await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
            new CollectingLogger<PackageCache>(), [3_000_000_000]);
        var earlier = PackageRewriter.VersionFor("1.2.0-beta.3", 755, 3_000_000_000, 1, 200);
        var later = PackageRewriter.VersionFor("1.2.0-beta.3", 755, 10_000_000_000, 1, 200);

        var result = await RestoreAsync(fixture.DirectoryPath, app.Urls.Single() + "/v3/index.json",
            "Acme.Core", "1.2.0-beta.3.pr.755.run.r*", exact: false);

        if (result.ExitCode != 0) throw new InvalidOperationException($"Floating dotnet restore failed:\n{result.Output}");
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.ProjectDirectory, "obj", "project.assets.json")));
        var libraries = assets.RootElement.GetProperty("libraries");
        await Assert.That(libraries.TryGetProperty($"Acme.Core/{earlier}", out _)).IsFalse();
        await Assert.That(libraries.TryGetProperty($"Acme.Core/{later}", out _)).IsTrue();
    }

    private sealed class DownloadCounter
    {
        public int Packages;
    }

    private static async Task<WebApplication> StartReferenceFeedAsync(
        PackageRewriteFixture fixture, string version, string package, DownloadCounter counter)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/v3/index.json", () => Results.Json(new
        {
            version = "3.0.0",
            resources = new[] { new Dictionary<string, string>
            {
                ["@id"] = app.Urls.Single() + "/flat/",
                ["@type"] = "PackageBaseAddress/3.0.0"
            } }
        }));
        app.MapGet("/flat/{id}/index.json", (string id) => id.Equals("acme.core", StringComparison.OrdinalIgnoreCase)
            ? Results.Json(new { versions = new[] { version } }) : Results.NotFound());
        app.MapGet("/flat/{id}/{packageVersion}/{file}", (string id, string packageVersion, string file) =>
        {
            if (!id.Equals("acme.core", StringComparison.OrdinalIgnoreCase)
                || !packageVersion.Equals(version, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            if (file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                return Results.Bytes(PackageRewriter.ReadManifest(package), "application/xml");
            if (!file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            Interlocked.Increment(ref counter.Packages);
            return Results.File(package, "application/octet-stream");
        });
        await app.StartAsync();
        return app;
    }

    private static bool Has255ByteComponentLimit(string directory)
    {
        var probe = Path.Combine(directory, new string('a', 256));
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static async Task<(int ExitCode, string Output, string ProjectDirectory)> RestoreAsync(
        string directory, string feed, string id, string version, bool exact = true)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "restore-work");
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        var scratch = Path.Combine(project, "scratch");
        Directory.CreateDirectory(scratch);
        var projectFile = Path.Combine(project, "Restore.csproj");
        var configFile = Path.Combine(project, "NuGet.config");
        await File.WriteAllTextAsync(projectFile, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="{id}" Version="{(exact ? $"[{version}]" : version)}" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(configFile, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="fixture" value="{feed}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "restore", projectFile, "--configfile", configFile, "--packages", Path.Combine(project, "packages"),
            "--no-http-cache", "--force", "--disable-parallel", "--verbosity", "normal"
        }) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = scratch;
        start.Environment["TMP"] = scratch;
        start.Environment["TMPDIR"] = scratch;
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(project, "home");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(project, "http-cache");
        start.Environment["NUGET_SCRATCH"] = scratch;
        start.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(project, "plugin-cache");
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet restore.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, (await stdout) + (await stderr), project);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void CreateMinimalPackage(string path, string id, string version)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry($"{id}.nuspec");
        using (var stream = new StreamWriter(nuspec.Open(), Encoding.UTF8))
        {
            stream.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>{version}</version>
                    <authors>Example Author</authors>
                    <description>Unicode fixture</description>
                  </metadata>
                </package>
                """);
        }
        var payload = archive.CreateEntry("content/readme.txt");
        using var payloadWriter = new StreamWriter(payload.Open(), Encoding.UTF8);
        payloadWriter.Write("unicode payload");
    }
}
