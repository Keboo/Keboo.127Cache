using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Versioning;

namespace Keboo._127Cache.Tests;

public class PackageRewriteFeedTests
{
    [Test]
    public async Task SignedPackageAndDependencyIdentitiesAreConsistentAcrossTheFeed()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync(signCore: true);
        var signedPath = Path.Combine(fixture.DirectoryPath, "signed-Acme.Core.nupkg");
        using (var signed = new PackageArchiveReader(signedPath))
        {
            await Assert.That(await signed.IsSignedAsync(CancellationToken.None)).IsTrue();
            var signature = await signed.GetPrimarySignatureAsync(CancellationToken.None);
            await Assert.That(signature is not null).IsTrue();
            await Assert.That(signature!.Type).IsEqualTo(SignatureType.Author);
        }

        var logger = new CollectingLogger<PackageCache>();
        await using var app = await StartFeedAsync(fixture, github, logger);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        const string coreVersion = "1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200";
        const string widgetVersion = "2.0.0-rc.1.pr.755.run.100.attempt.1.artifact.200";
        const string emptyVersion = "1.2.3.4-pr.755.run.100.attempt.1.artifact.200";

        using (var index = JsonDocument.Parse(await client.GetStringAsync("flat/acme.core/index.json")))
            await Assert.That(index.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(coreVersion);
        using (var index = JsonDocument.Parse(await client.GetStringAsync("flat/acme.widget/index.json")))
            await Assert.That(index.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(widgetVersion);
        using (var index = JsonDocument.Parse(await client.GetStringAsync("flat/acme.empty/index.json")))
            await Assert.That(index.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(emptyVersion);

        var coreBytes = await client.GetByteArrayAsync($"flat/acme.core/{coreVersion}/acme.core.{coreVersion}.nupkg");
        using (var reader = new PackageArchiveReader(new MemoryStream(coreBytes)))
        {
            await Assert.That(reader.GetIdentity().Id).IsEqualTo("Acme.Core");
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(coreVersion);
            await Assert.That(await reader.IsSignedAsync(CancellationToken.None)).IsFalse();
        }
        using (var original = ZipFile.OpenRead(signedPath))
        using (var archive = new ZipArchive(new MemoryStream(coreBytes), ZipArchiveMode.Read))
        {
            await Assert.That(archive.GetEntry(".signature.p7s") is null).IsTrue();
            await Assert.That(archive.GetEntry("docs/.signature.p7s") is not null).IsTrue();
            await Assert.That(archive.GetEntry(".SIGNATURE.P7S") is not null).IsTrue();
            await Assert.That(archive.Entries.Count).IsEqualTo(original.Entries.Count - 1);
            foreach (var source in original.Entries.Where(x => x.FullName != ".signature.p7s"))
            {
                var rewritten = archive.GetEntry(source.FullName);
                await Assert.That(rewritten is not null).IsTrue();
                await Assert.That(rewritten!.ExternalAttributes).IsEqualTo(source.ExternalAttributes);
                await Assert.That(rewritten.LastWriteTime).IsEqualTo(source.LastWriteTime);
                if (source.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                    || source.FullName.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase)) continue;
                using var input = source.Open();
                using var output = rewritten.Open();
                await Assert.That(Convert.ToHexString(SHA256.HashData(output))).IsEqualTo(Convert.ToHexString(SHA256.HashData(input)));
            }
            var manifest = PackageRewriteFixture.ReadXml(archive, "Acme.Core.nuspec");
            await Assert.That(manifest.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(coreVersion);
            var properties = PackageRewriteFixture.ReadXml(archive, PackageRewriteFixture.CorePropertiesPath);
            await Assert.That(properties.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(coreVersion);
            await Assert.That(properties.Descendants().Single(x => x.Name.LocalName == "identifier").Value).IsEqualTo("Acme.Core");
        }
        var flatManifest = XDocument.Parse(await client.GetStringAsync($"flat/acme.core/{coreVersion}/acme.core.nuspec"));
        await Assert.That(flatManifest.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(coreVersion);

        var widgetBytes = await client.GetByteArrayAsync($"flat/acme.widget/{widgetVersion}/acme.widget.{widgetVersion}.nupkg");
        using (var archive = new ZipArchive(new MemoryStream(widgetBytes), ZipArchiveMode.Read))
        {
            var manifest = PackageRewriteFixture.ReadXml(archive, "Acme.Widget.nuspec");
            await Assert.That(manifest.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(widgetVersion);
            var dependencies = manifest.Descendants().Where(x => x.Name.LocalName == "dependency").ToArray();
            await Assert.That((string?)dependencies[0].Attribute("version")).IsEqualTo($"[{coreVersion}]");
            await Assert.That((string?)dependencies[2].Attribute("version")).IsEqualTo($"[{coreVersion}]");
            await Assert.That((string?)dependencies[1].Attribute("version")).IsEqualTo("[3.1.0,4.0.0)");
            await Assert.That((string?)dependencies[3].Attribute("version")).IsEqualTo("(,5.0.0]");
            var properties = PackageRewriteFixture.ReadXml(archive, PackageRewriteFixture.CorePropertiesPath);
            await Assert.That(properties.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(widgetVersion);
        }

        using (var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.widget/index.json")))
        {
            var entry = registration.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetProperty("catalogEntry");
            await Assert.That(entry.GetProperty("id").GetString()).IsEqualTo("Acme.Widget");
            await Assert.That(entry.GetProperty("version").GetString()).IsEqualTo(widgetVersion);
            var groups = entry.GetProperty("dependencyGroups").EnumerateArray().ToArray();
            await Assert.That(groups.Length).IsEqualTo(2);
            foreach (var group in groups)
            {
                var dependencies = group.GetProperty("dependencies").EnumerateArray().ToArray();
                var internalDependency = dependencies.Single(x => x.GetProperty("id").GetString()!.Equals("Acme.Core", StringComparison.OrdinalIgnoreCase));
                await Assert.That(internalDependency.GetProperty("range").GetString())
                    .IsEqualTo(VersionRange.Parse($"[{coreVersion}]").ToNormalizedString());
                var external = dependencies.Single(x => x.GetProperty("id").GetString()!.StartsWith("External.", StringComparison.Ordinal));
                var originalRange = external.GetProperty("id").GetString() == "External.Alpha" ? "[3.1.0,4.0.0)" : "(,5.0.0]";
                await Assert.That(external.GetProperty("range").GetString()).IsEqualTo(VersionRange.Parse(originalRange).ToNormalizedString());
            }
        }
        using (var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.core/index.json")))
        {
            var entry = registration.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetProperty("catalogEntry");
            await Assert.That(entry.GetProperty("version").GetString()).IsEqualTo(coreVersion);
            await Assert.That(entry.GetProperty("packageContent").GetString()!.Contains(coreVersion, StringComparison.Ordinal)).IsTrue();
        }
        using (var search = JsonDocument.Parse(await client.GetStringAsync("query?q=Acme&prerelease=true&semVerLevel=2.0.0")))
        {
            var data = search.RootElement.GetProperty("data").EnumerateArray().ToArray();
            await Assert.That(data.Length).IsEqualTo(3);
            foreach (var (id, version) in new[] { ("Acme.Core", coreVersion), ("Acme.Widget", widgetVersion), ("Acme.Empty", emptyVersion) })
            {
                var item = data.Single(x => x.GetProperty("id").GetString() == id);
                await Assert.That(item.GetProperty("version").GetString()).IsEqualTo(version);
                await Assert.That(item.GetProperty("versions")[0].GetProperty("version").GetString()).IsEqualTo(version);
            }
        }
        using (var autocomplete = JsonDocument.Parse(await client.GetStringAsync("autocomplete?id=Acme.Core&prerelease=true&semVerLevel=2.0.0")))
            await Assert.That(autocomplete.RootElement.GetProperty("data")[0].GetString()).IsEqualTo(coreVersion);
        using (var builds = JsonDocument.Parse(await client.GetStringAsync("builds")))
            await Assert.That(builds.RootElement[0].GetProperty("version").GetString()).IsEqualTo(coreVersion);
        await Assert.That(logger.Messages.Count(x => x.Level == LogLevel.Information
            && x.Message.Contains("signature", StringComparison.OrdinalIgnoreCase)
            && x.Message.Contains("Acme.Core", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task LegacySchemaRegeneratesPackagesFromRawArtifactsAndUpgradesCatalog()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        var legacyVersion = "1.2.0-pr.755.run.100.attempt.1.artifact.200";
        var expiredPackages = github.Packages with { Expired = true };
        var expiredVersion = github.Version with { Expired = true };
        var legacyBuild = new BuildEntry(100, 1, 755, "head", "1.2.0-beta.3", legacyVersion, expiredPackages, expiredVersion);
        var run = new RunArtifacts(100, 1, 755, "head", expiredPackages, expiredVersion);
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, [run]), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "catalog.json"),
            new { options.Repository, Builds = new[] { legacyBuild } }, CancellationToken.None);
        var oldDirectory = Path.Combine(options.CacheDirectory, "packages", "discovered", "755.100.1.200");
        Directory.CreateDirectory(oldDirectory);
        var oldIndex = Path.Combine(oldDirectory, "packages.json");
        await JsonSettings.WriteAtomicAsync(oldIndex,
            new[] { new CachedPackage("Acme.Core", "1.2.0-beta.3", legacyVersion) }, CancellationToken.None);
        var oldPackage = Path.Combine(oldDirectory, $"acme.core.{legacyVersion}.nupkg");
        await File.WriteAllTextAsync(oldPackage, "legacy bytes must not be used or removed");
        var oldIndexContents = await File.ReadAllTextAsync(oldIndex);

        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var packages = new PackageCache(options, artifacts);
        var catalog = new PackageCatalog(options, github, memory, artifacts, packages);
        var build = (await catalog.GetBuildsAsync(CancellationToken.None)).Single();
        const string expected = "1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200";
        await Assert.That(build.Version).IsEqualTo(expected);
        var inventory = await packages.GetPackagesAsync(build, CancellationToken.None);
        await Assert.That(inventory.Length).IsEqualTo(3);
        await Assert.That(inventory.Single(x => x.Id == "Acme.Core").Version).IsEqualTo(expected);
        var rewrittenPath = await packages.GetAsync("acme.core", build, CancellationToken.None);
        await Assert.That(rewrittenPath.Contains(Path.Combine("packages", "discovered", "v2"), StringComparison.OrdinalIgnoreCase)).IsTrue();
        using (var reader = new PackageArchiveReader(rewrittenPath))
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(expected);
        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(Path.Combine(options.CacheDirectory, "catalog.json")),
            JsonSettings.Options)!;
        await Assert.That(saved.SchemaVersion).IsEqualTo(2);
        await Assert.That(saved.Builds.Single().Version).IsEqualTo(expected);
        await Assert.That(await File.ReadAllTextAsync(oldIndex)).IsEqualTo(oldIndexContents);
        await Assert.That(await File.ReadAllTextAsync(oldPackage)).IsEqualTo("legacy bytes must not be used or removed");
        await Assert.That(File.Exists(Path.Combine(options.CacheDirectory, "artifacts", "200.zip"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(options.CacheDirectory, "artifacts", "201.zip"))).IsTrue();
        await Assert.That(github.Downloads).IsEqualTo(0);

        var writtenAt = File.GetLastWriteTimeUtc(rewrittenPath);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        var restartedPackages = new PackageCache(options, new ArtifactCache(options, github));
        var restartedCatalog = new PackageCatalog(options, github, restartedMemory, new ArtifactCache(options, github), restartedPackages);
        var restartedBuild = (await restartedCatalog.GetBuildsAsync(CancellationToken.None)).Single();
        await Assert.That(await restartedPackages.GetAsync("Acme.Core", restartedBuild, CancellationToken.None)).IsEqualTo(rewrittenPath);
        await Assert.That(File.GetLastWriteTimeUtc(rewrittenPath)).IsEqualTo(writtenAt);
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task LegacyPackageIsNotServedWhenTheRawArtifactHasExpired()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache") };
        var legacyVersion = "1.2.0-pr.755.run.100.attempt.1.artifact.200";
        var build = new BuildEntry(100, 1, 755, "head", "1.2.0-beta.3", legacyVersion,
            github.Packages with { Expired = true }, github.Version);
        var oldDirectory = Path.Combine(options.CacheDirectory, "packages", "discovered", "755.100.1.200");
        Directory.CreateDirectory(oldDirectory);
        var oldIndex = Path.Combine(oldDirectory, "packages.json");
        await JsonSettings.WriteAtomicAsync(oldIndex,
            new[] { new CachedPackage("Acme.Core", "1.2.0-beta.3", legacyVersion) }, CancellationToken.None);
        var oldPackage = Path.Combine(oldDirectory, $"acme.core.{legacyVersion}.nupkg");
        await File.WriteAllTextAsync(oldPackage, "legacy bytes");
        var packages = new PackageCache(options, new ArtifactCache(options, github));

        await Assert.That(async () => await packages.GetAsync("Acme.Core", build, CancellationToken.None)).ThrowsExactly<IOException>();
        await Assert.That(await File.ReadAllTextAsync(oldPackage)).IsEqualTo("legacy bytes");
        await Assert.That(File.Exists(oldIndex)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(options.CacheDirectory, "packages", "discovered", "v2"))).IsFalse();
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task NewerCacheSchemaIsRejectedWithoutChangingTheCatalog()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache") };
        Directory.CreateDirectory(options.CacheDirectory);
        var catalogPath = Path.Combine(options.CacheDirectory, "catalog.json");
        await JsonSettings.WriteAtomicAsync(catalogPath, new CatalogFile(options.Repository, [], CacheSchema.Version + 1), CancellationToken.None);
        var contents = await File.ReadAllTextAsync(catalogPath);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var packages = new PackageCache(options, artifacts);

        await Assert.That(() => new PackageCatalog(options, github, memory, artifacts, packages)).ThrowsExactly<InvalidDataException>();
        await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(contents);
    }

    [Test]
    public async Task FeedOrdersLargeRunIdsNumericallyWithinTheSamePrerelease()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        github.Runs = [
            new(3_000_000_000, 1, "old", "completed", "success", "pull_request", [new(755)]),
            new(10_000_000_000, 1, "new", "completed", "success", "pull_request", [new(755)])
        ];
        var logger = new CollectingLogger<PackageCache>();
        await using var app = await StartFeedAsync(fixture, github, logger, [3_000_000_000]);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        const string earlier = "1.2.0-beta.3.pr.755.run.3000000000.attempt.1.artifact.200";
        const string later = "1.2.0-beta.3.pr.755.run.10000000000.attempt.1.artifact.200";

        using (var versions = JsonDocument.Parse(await client.GetStringAsync("flat/acme.core/index.json")))
        {
            await Assert.That(versions.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(earlier);
            await Assert.That(versions.RootElement.GetProperty("versions")[1].GetString()).IsEqualTo(later);
        }
        using (var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.core/index.json")))
        {
            var page = registration.RootElement.GetProperty("items")[0];
            await Assert.That(page.GetProperty("lower").GetString()).IsEqualTo(earlier);
            await Assert.That(page.GetProperty("upper").GetString()).IsEqualTo(later);
        }
        using (var search = JsonDocument.Parse(await client.GetStringAsync("query?q=Acme.Core&prerelease=true&semVerLevel=2.0.0")))
            await Assert.That(search.RootElement.GetProperty("data")[0].GetProperty("version").GetString()).IsEqualTo(later);
    }

    [Test]
    public async Task CatalogPreservesSemVerPrecedenceAcrossOriginalLabels()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions
        {
            CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"),
            PullRequests = [755],
            RunIds = [10, 20, 30, 40]
        };
        Directory.CreateDirectory(options.CacheDirectory);
        var originals = new[]
        {
            (10L, "1.2.0-beta.3"),
            (20L, "1.2.0-alpha.10"),
            (30L, "1.2.0-rc.1"),
            (40L, "1.2.0-alpha.2")
        };
        github.Runs = originals.Select(x => new WorkflowRun(x.Item1, 1, "head", "completed", "success", "pull_request", [new(755)])).ToArray();
        var runs = originals.Select(x => new RunArtifacts(x.Item1, 1, 755, "head", github.Packages, github.Version)).ToList();
        var builds = originals.Select(x => new BuildEntry(x.Item1, 1, 755, "head", x.Item2,
            PackageRewriter.VersionFor(x.Item2, 755, x.Item1, 1, github.Packages.Id), github.Packages, github.Version)).ToList();
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, runs), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "catalog.json"),
            new CatalogFile(options.Repository, builds, CacheSchema.Version), CancellationToken.None);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts));

        var actual = (await catalog.GetBuildsAsync(CancellationToken.None)).Select(x => x.OriginalVersion).ToArray();

        await Assert.That(string.Join(",", actual)).IsEqualTo("1.2.0-alpha.2,1.2.0-alpha.10,1.2.0-beta.3,1.2.0-rc.1");
    }

    private static async Task<WebApplication> StartFeedAsync(PackageRewriteFixture fixture, FixtureGitHub github,
        CollectingLogger<PackageCache> logger, long[]? runIds = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:CacheDirectory"] = Path.Combine(fixture.DirectoryPath, "cache"),
            ["Proxy:PullRequests:0"] = "755"
        });
        builder.Logging.ClearProviders();
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(github);
        builder.Services.AddSingleton<ILogger<PackageCache>>(logger);
        var app = builder.Build();
        app.Services.GetRequiredService<ProxyOptions>().RunIds = runIds ?? [];
        app.MapProxy();
        await app.StartAsync();
        app.Services.GetRequiredService<ProxyOptions>().BaseUrl = app.Urls.Single();
        return app;
    }
}
