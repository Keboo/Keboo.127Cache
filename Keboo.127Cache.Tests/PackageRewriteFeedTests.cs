using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Keboo._127Cache.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
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
        const string coreVersion = "1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200";
        const string widgetVersion = "2.0.0-rc.1.pr.755.run.r0000000000000000100.attempt.1.artifact.200";
        const string emptyVersion = "1.2.3.4-pr.755.run.r0000000000000000100.attempt.1.artifact.200";

        using (var index = JsonDocument.Parse(await CatalogTestSynchronization.GetStringWhenSettledAsync(
            client, app.Services.GetRequiredService<PackageCatalog>(), "flat/acme.core/index.json")))
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
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        var legacyVersion = "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200";
        var expiredPackages = github.Packages with { Expired = true };
        var expiredVersion = github.Version with { Expired = true };
        var legacyBuild = new BuildEntry(100, 1, 755, "head", "1.2.0-beta.3", legacyVersion, expiredPackages, expiredVersion);
        var run = new RunArtifacts(100, 1, 755, "head", expiredPackages, expiredVersion, WorkflowVerified: true);
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, [run], SchemaVersion: 2), CancellationToken.None);
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
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, packages);
        var build = (await CatalogTestSynchronization.WhenSettledAsync(catalog,
            () => catalog.GetBuildsAsync(CancellationToken.None))).Single();
        const string expected = "1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200";
        await Assert.That(build.Version).IsEqualTo(expected);
        var inventory = await packages.GetPackagesAsync(build, CancellationToken.None);
        await Assert.That(inventory.Length).IsEqualTo(3);
        await Assert.That(inventory.Single(x => x.Id == "Acme.Core").Version).IsEqualTo(expected);
        var rewrittenPath = await packages.GetAsync("acme.core", build, CancellationToken.None);
        await Assert.That(rewrittenPath.Contains(Path.Combine("packages", "discovered", $"v{CacheSchema.Version}"), StringComparison.OrdinalIgnoreCase)).IsTrue();
        using (var reader = new PackageArchiveReader(rewrittenPath))
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(expected);
        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(Path.Combine(options.CacheDirectory, "catalog.json")),
            JsonSettings.Options)!;
        await Assert.That(saved.SchemaVersion).IsEqualTo(CacheSchema.Version);
        await Assert.That(saved.Builds.Single().Version).IsEqualTo(expected);
        await Assert.That(await File.ReadAllTextAsync(oldIndex)).IsEqualTo(oldIndexContents);
        await Assert.That(await File.ReadAllTextAsync(oldPackage)).IsEqualTo("legacy bytes must not be used or removed");
        await Assert.That(File.Exists(Path.Combine(options.CacheDirectory, "artifacts", "200.zip"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(options.CacheDirectory, "artifacts", "201.zip"))).IsTrue();
        await Assert.That(github.Downloads).IsEqualTo(0);

        var writtenAt = File.GetLastWriteTimeUtc(rewrittenPath);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        var restartedPackages = new PackageCache(options, new ArtifactCache(options, github));
        await using var restartedCatalog = new PackageCatalog(options, github, restartedMemory, new ArtifactCache(options, github), restartedPackages);
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
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache") };
        var legacyVersion = "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200";
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
        await Assert.That(Directory.Exists(Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}"))).IsFalse();
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task MissingExpiredLegacyBuildDoesNotHideHealthyPackagesAndCanRecoverAfterRestart()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cacheDirectory = Path.Combine(fixture.DirectoryPath, "cache");
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = cacheDirectory, PullRequests = [755] };
        github.SeedRawCache(cacheDirectory);
        github.FailDownloads = true;
        var unavailableArtifact = github.Packages with { Id = 300, Expired = true };
        var unavailable = new BuildEntry(99, 1, 755, "old", "1.2.0-beta.3",
            "1.2.0-pr.755.run.r0000000000000000099.attempt.1.artifact.300", unavailableArtifact, github.Version);
        var healthy = new BuildEntry(100, 1, 755, "head", "1.2.0-beta.3",
            "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200", github.Packages, github.Version);
        var runs = new[] { unavailable, healthy }.Select(x =>
            new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact, WorkflowVerified: true)).ToList();
        await JsonSettings.WriteAtomicAsync(Path.Combine(cacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, runs, SchemaVersion: 2), CancellationToken.None);
        var catalogPath = Path.Combine(cacheDirectory, "catalog.json");
        await JsonSettings.WriteAtomicAsync(catalogPath,
            new { options.Repository, Builds = new[] { unavailable, healthy } }, CancellationToken.None);
        var originalCatalog = await File.ReadAllTextAsync(catalogPath);
        var oldDirectories = new[] { unavailable, healthy }.Select(build =>
            Path.Combine(cacheDirectory, "packages", "discovered",
                $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}")).ToArray();
        foreach (var directory in oldDirectories)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "legacy.nupkg"), "recoverable legacy bytes");
            await File.WriteAllTextAsync(Path.Combine(directory, "packages.json"), "recoverable legacy inventory");
        }

        var catalogLogger = new CollectingLogger<PackageCatalog>();
        await using (var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>(),
            catalogLogger: catalogLogger))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            using var index = JsonDocument.Parse(await CatalogTestSynchronization.GetStringWhenSettledAsync(
                client, app.Services.GetRequiredService<PackageCatalog>(), "flat/acme.core/index.json"));
            var versions = index.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()).ToArray();
            await Assert.That(versions.Length).IsEqualTo(1);
            await Assert.That(versions[0]).IsEqualTo("1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200");
            var bytes = await client.GetByteArrayAsync($"flat/acme.core/{versions[0]}/acme.core.{versions[0]}.nupkg");
            using var reader = new PackageArchiveReader(new MemoryStream(bytes));
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(versions[0]);
        }
        await Assert.That(catalogLogger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("99", StringComparison.Ordinal)
            && x.Message.Contains("300", StringComparison.Ordinal)
            && x.Exception?.Message.Contains("expired", StringComparison.OrdinalIgnoreCase) == true)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(originalCatalog);
        foreach (var directory in oldDirectories)
        {
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(directory, "legacy.nupkg"))).IsEqualTo("recoverable legacy bytes");
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(directory, "packages.json"))).IsEqualTo("recoverable legacy inventory");
        }

        await using (var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>()))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            using (var before = JsonDocument.Parse(await client.GetStringAsync("flat/acme.core/index.json")))
                await Assert.That(before.RootElement.GetProperty("versions").GetArrayLength()).IsEqualTo(1);
            await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(originalCatalog);

            File.Copy(Path.Combine(cacheDirectory, "artifacts", "200.zip"), Path.Combine(cacheDirectory, "artifacts", "300.zip"));
            using var recovered = JsonDocument.Parse(await client.GetStringAsync("flat/acme.core/index.json"));
            var versions = recovered.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()).ToArray();
            await Assert.That(string.Join(",", versions)).IsEqualTo(
                "1.2.0-beta.3.pr.755.run.r0000000000000000099.attempt.1.artifact.300,1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200");
            var bytes = await client.GetByteArrayAsync($"flat/acme.core/{versions[0]}/acme.core.{versions[0]}.nupkg");
            using var reader = new PackageArchiveReader(new MemoryStream(bytes));
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(versions[0]);
        }
        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(catalogPath), JsonSettings.Options)!;
        await Assert.That(saved.SchemaVersion).IsEqualTo(CacheSchema.Version);
        await Assert.That(saved.Builds.Count).IsEqualTo(2);
        await Assert.That(github.Downloads).IsEqualTo(0);
        foreach (var directory in oldDirectories)
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(directory, "legacy.nupkg"))).IsEqualTo("recoverable legacy bytes");
    }

    [Test]
    public async Task ClosedLegacyBuildMustAlsoRegenerateBeforeTheCatalogIsUpgraded()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        var closedArtifact = github.Packages with { Id = 300, Expired = true };
        var open = new BuildEntry(100, 1, 755, "open", "1.2.0-beta.3",
            "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200", github.Packages, github.Version);
        var closed = new BuildEntry(99, 1, 756, "closed", "1.2.0-beta.3",
            "1.2.0-pr.756.run.r0000000000000000099.attempt.1.artifact.300", closedArtifact, github.Version);
        var runs = new[] { open, closed }.Select(x =>
            new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact, WorkflowVerified: true)).ToList();
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, runs, SchemaVersion: 2), CancellationToken.None);
        var catalogPath = Path.Combine(options.CacheDirectory, "catalog.json");
        await JsonSettings.WriteAtomicAsync(catalogPath, new { options.Repository, Builds = new[] { open, closed } }, CancellationToken.None);
        var originalCatalog = await File.ReadAllTextAsync(catalogPath);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var logger = new CollectingLogger<PackageCatalog>();
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts), logger);

        var visible = await CatalogTestSynchronization.WhenSettledAsync(catalog, () => catalog.GetPackagesAsync(CancellationToken.None));
        await Assert.That(visible.Length).IsEqualTo(3);
        await Assert.That(visible.All(x => x.Build.PullRequest == 755)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(originalCatalog);
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("756", StringComparison.Ordinal)
            && x.Message.Contains("300", StringComparison.Ordinal))).IsTrue();

        File.Copy(Path.Combine(options.CacheDirectory, "artifacts", "200.zip"),
            Path.Combine(options.CacheDirectory, "artifacts", "300.zip"));
        visible = await CatalogTestSynchronization.WhenSettledAsync(catalog,
            () => catalog.GetPackagesAsync(CancellationToken.None));
        await Assert.That(visible.Length).IsEqualTo(3);
        await Assert.That(visible.All(x => x.Build.PullRequest == 755)).IsTrue();
        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(catalogPath), JsonSettings.Options)!;
        await Assert.That(saved.SchemaVersion).IsEqualTo(CacheSchema.Version);
        await Assert.That(saved.Builds.Single(x => x.PullRequest == 756).Version)
            .IsEqualTo("1.2.0-beta.3.pr.756.run.r0000000000000000099.attempt.1.artifact.300");
        await Assert.That(File.Exists(Path.Combine(options.CacheDirectory, "packages", "discovered",
            $"v{CacheSchema.Version}", "756.99.1.300", "packages.json"))).IsTrue();
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task LongPrereleaseUsesAFileSystemSafeCacheNameWithoutChangingThePublicVersion()
    {
        using var fixture = new PackageRewriteFixture();
        var original = "1.2.0-preview." + new string('a', 163);
        var github = await fixture.CreateGitHubAsync(coreVersion: original);
        await using var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var expected = PackageRewriter.VersionFor(original, 755, 100, 1, 200);
        await Assert.That(NuGetVersion.Parse(expected).ToNormalizedString()).IsEqualTo(expected);
        await Assert.That($"acme.core.{expected}.nupkg.sha512".Length).IsEqualTo(255);
        var catalog = app.Services.GetRequiredService<PackageCatalog>();
        var build = (await CatalogTestSynchronization.WhenSettledAsync(catalog, () => catalog.GetBuildsAsync(CancellationToken.None))).Single();
        var path = await app.Services.GetRequiredService<PackageCache>().GetAsync("Acme.Core", build, CancellationToken.None);
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(Path.GetFileName(path).Length <= 255).IsTrue();

        using (var index = JsonDocument.Parse(await client.GetStringAsync("flat/acme.core/index.json")))
            await Assert.That(index.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(expected);
        var bytes = await client.GetByteArrayAsync($"flat/acme.core/{expected}/acme.core.{expected}.nupkg");
        using (var reader = new PackageArchiveReader(new MemoryStream(bytes)))
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(expected);
        var manifest = XDocument.Parse(await client.GetStringAsync($"flat/acme.core/{expected}/acme.core.nuspec"));
        await Assert.That(manifest.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(expected);
        using (var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.core/index.json")))
            await Assert.That(registration.RootElement.GetProperty("items")[0].GetProperty("items")[0]
                .GetProperty("catalogEntry").GetProperty("version").GetString()).IsEqualTo(expected);
        using (var widget = JsonDocument.Parse(await client.GetStringAsync("registration/acme.widget/index.json")))
            await Assert.That(widget.RootElement.GetProperty("items")[0].GetProperty("items")[0]
                .GetProperty("catalogEntry").GetProperty("dependencyGroups")[0].GetProperty("dependencies")[0]
                .GetProperty("range").GetString()).IsEqualTo(VersionRange.Parse($"[{expected}]").ToNormalizedString());

        using var cached = new PackageArchiveReader(path);
        await Assert.That(cached.GetIdentity().Version.ToNormalizedString()).IsEqualTo(expected);
    }

    [Test]
    public async Task LegacyCatalogIsNotUpgradedWhenANewInventoryHasMissingFiles()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        var build = new BuildEntry(100, 1, 755, "head", "1.2.0-beta.3",
            "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200", github.Packages, github.Version);
        var catalogPath = Path.Combine(options.CacheDirectory, "catalog.json");
        await JsonSettings.WriteAtomicAsync(catalogPath, new { options.Repository, Builds = new[] { build } }, CancellationToken.None);
        var originalCatalog = await File.ReadAllTextAsync(catalogPath);
        var incompleteDirectory = Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}", "755.100.1.200");
        Directory.CreateDirectory(incompleteDirectory);
        var inventoryPath = Path.Combine(incompleteDirectory, "packages.json");
        await JsonSettings.WriteAtomicAsync(inventoryPath,
            new[] { new CachedPackage("Acme.Core", "1.2.0-beta.3",
                "1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200") }, CancellationToken.None);
        var inventory = await File.ReadAllTextAsync(inventoryPath);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var logger = new CollectingLogger<PackageCatalog>();
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts), logger);

        var available = await catalog.GetPackagesAsync(CancellationToken.None);

        await Assert.That(available.Length).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(originalCatalog);
        await Assert.That(await File.ReadAllTextAsync(inventoryPath)).IsEqualTo(inventory);
        await Assert.That(File.Exists(Path.Combine(options.CacheDirectory, "artifacts", "200.zip"))).IsTrue();
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("100", StringComparison.Ordinal)
            && x.Message.Contains("200", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("mismatched-id")]
    [Arguments("missing-manifest")]
    [Arguments("unexpected-metadata")]
    [Arguments("invalid-version")]
    public async Task LegacyCatalogDoesNotAdvertiseACorruptOrMismatchedCachedPackage(string corruption)
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        var build = new BuildEntry(100, 1, 755, "head", "1.2.0-beta.3",
            "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200", github.Packages, github.Version);
        var catalogPath = Path.Combine(options.CacheDirectory, "catalog.json");
        await JsonSettings.WriteAtomicAsync(catalogPath, new { options.Repository, Builds = new[] { build } }, CancellationToken.None);
        var originalCatalog = await File.ReadAllTextAsync(catalogPath);
        var directory = Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}", "755.100.1.200");
        Directory.CreateDirectory(directory);
        var expected = PackageRewriter.VersionFor(build.OriginalVersion, 755, 100, 1, 200);
        await JsonSettings.WriteAtomicAsync(Path.Combine(directory, "packages.json"),
            new[] { new CachedPackage("Acme.Core", build.OriginalVersion, expected) }, CancellationToken.None);
        var packagePath = Path.Combine(directory, $"acme.core.{expected}.nupkg");
        if (corruption == "missing-manifest")
        {
            using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
            archive.CreateEntry("payload.txt");
        }
        else if (corruption == "unexpected-metadata")
        {
            using var otherFixture = new PackageRewriteFixture();
            File.Copy(otherFixture.CreatePackage("Acme.Core", expected + "+unexpected"), packagePath);
        }
        else if (corruption == "invalid-version")
        {
            using var otherFixture = new PackageRewriteFixture();
            File.Copy(otherFixture.CreatePackage("Acme.Core", "invalid-version"), packagePath);
        }
        else File.Copy(fixture.CreatePackage("Acme.Other", "9.0.0"), packagePath);
        var originalBytes = await File.ReadAllBytesAsync(packagePath);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var logger = new CollectingLogger<PackageCatalog>();
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts), logger);

        var available = await catalog.GetPackagesAsync(CancellationToken.None);

        await Assert.That(available.Length).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(originalCatalog);
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(packagePath)))
            .IsEqualTo(Convert.ToHexString(originalBytes));
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && (corruption == "missing-manifest" ? x.Exception is PackagingException
                : corruption == "invalid-version" ? x.Exception is InvalidDataException or PackagingException
                : x.Exception?.Message.Contains("mismatched identity", StringComparison.OrdinalIgnoreCase) == true))).IsTrue();
    }

    [Test]
    public async Task CurrentSchemaLegacyStyleFilenamesRemainUsableWithoutRawArtifacts()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var packages = new PackageCache(options, artifacts);
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, packages);
        var build = (await catalog.GetBuildsAsync(CancellationToken.None)).Single();
        var inventory = await packages.GetPackagesAsync(build, CancellationToken.None);
        foreach (var package in inventory)
        {
            var path = await packages.GetAsync(package.Id, build, CancellationToken.None);
            var legacyPath = Path.Combine(Path.GetDirectoryName(path)!, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");
            if (!path.Equals(legacyPath, StringComparison.OrdinalIgnoreCase)) File.Move(path, legacyPath);
        }
        File.Delete(Path.Combine(options.CacheDirectory, "artifacts", "200.zip"));

        var restarted = new PackageCache(options, new ArtifactCache(options, github));
        foreach (var package in inventory)
        {
            var path = await restarted.GetAsync(package.Id, build, CancellationToken.None);
            await Assert.That(Path.GetFileName(path)).IsEqualTo($"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");
            using var reader = new PackageArchiveReader(path);
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(package.Version);
        }
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task MissingVersionArtifactIsIsolatedAndRetriedWhenRestored()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"), PullRequests = [755], RunIds = [100, 101] };
        github.SeedRawCache(options.CacheDirectory);
        github.FailDownloads = true;
        var unavailableVersion = github.Version with { Id = 301, Expired = true };
        var runs = new List<RunArtifacts>
        {
            new(100, 1, 755, "healthy", github.Packages, github.Version),
            new(101, 1, 755, "unavailable", github.Packages, unavailableVersion)
        };
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, runs, SchemaVersion: 2), CancellationToken.None);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        var logger = new CollectingLogger<PackageCatalog>();
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts), logger);

        var healthy = await CatalogTestSynchronization.WhenSettledAsync(catalog, () => catalog.GetPackagesAsync(CancellationToken.None));
        await Assert.That(healthy.Length).IsEqualTo(3);
        await Assert.That(healthy.All(x => x.Build.RunId == 100)).IsTrue();
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("101", StringComparison.Ordinal)
            && x.Message.Contains("301", StringComparison.Ordinal)
            && x.Exception?.Message.Contains("expired", StringComparison.OrdinalIgnoreCase) == true)).IsTrue();

        File.Copy(Path.Combine(options.CacheDirectory, "artifacts", "201.zip"),
            Path.Combine(options.CacheDirectory, "artifacts", "301.zip"));
        var recovered = await catalog.GetPackagesAsync(CancellationToken.None);
        await Assert.That(recovered.Length).IsEqualTo(6);
        await Assert.That(recovered.Count(x => x.Build.RunId == 101)).IsEqualTo(3);
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task BuildsEndpointAndInstallerSelectionExcludeNewestInvalidBuild()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = cache, PullRequests = [755] };
        github.SeedRawCache(cache);
        github.FailDownloads = true;

        var duplicateOne = Path.Combine(fixture.DirectoryPath, "Acme.Core.nupkg");
        var duplicateTwo = Path.Combine(fixture.DirectoryPath, "Acme.Core.Copy.nupkg");
        File.Copy(duplicateOne, duplicateTwo);
        var badArtifactPath = Path.Combine(cache, "artifacts", "300.zip");
        using (var archive = ZipFile.Open(badArtifactPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(duplicateOne, "Acme.Core.First.nupkg");
            archive.CreateEntryFromFile(duplicateTwo, "Acme.Core.Second.nupkg");
        }
        var badArtifact = new Artifact(300, "packages", true,
            "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(badArtifactPath))).ToLowerInvariant());
        var healthyVersion = PackageRewriter.VersionFor("1.2.0-beta.3", 755, 100, 1, github.Packages.Id);
        var invalidVersion = PackageRewriter.VersionFor("1.2.0-beta.3", 755, 101, 1, badArtifact.Id);
        var healthy = new BuildEntry(100, 1, 755, "healthy", "1.2.0-beta.3", healthyVersion, github.Packages, github.Version);
        var invalid = new BuildEntry(101, 1, 755, "invalid", "1.2.0-beta.3", invalidVersion, badArtifact, github.Version);
        var builds = new[] { healthy, invalid };
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, builds.Select(x =>
                new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact, WorkflowVerified: true)).ToList(), SchemaVersion: 2),
            CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
            new CatalogFile(options.Repository, builds.ToList(), CacheSchema.Version), CancellationToken.None);

        var logger = new CollectingLogger<PackageCatalog>();
        await using var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>(), [100, 101], catalogLogger: logger);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        using var response = JsonDocument.Parse(await client.GetStringAsync("builds"));
        var advertised = response.RootElement.EnumerateArray().ToArray();
        await Assert.That(advertised.Length).IsEqualTo(1);
        await Assert.That(advertised[0].GetProperty("runId").GetInt64()).IsEqualTo(100);
        await Assert.That(advertised[0].GetProperty("version").GetString()).IsEqualTo(healthyVersion);

        var selected = advertised.Where(x => x.GetProperty("pullRequest").GetInt32() == 755)
            .OrderByDescending(x => x.GetProperty("runId").GetInt64())
            .ThenByDescending(x => x.GetProperty("attempt").GetInt32())
            .ThenByDescending(x => x.GetProperty("packages").GetProperty("id").GetInt64())
            .First();
        var selectedVersion = selected.GetProperty("version").GetString();
        using var package = await client.GetAsync($"flat/acme.core/{selectedVersion}/acme.core.{selectedVersion}.nupkg");
        await Assert.That((int)package.StatusCode).IsEqualTo(200);
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("101", StringComparison.Ordinal)
            && x.Message.Contains("300", StringComparison.Ordinal)
            && x.Exception?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)).IsTrue();
    }

    [Test]
    public async Task NewerCacheSchemaIsRejectedWithoutChangingTheCatalog()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache") };
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
    public async Task FeedOrdersLargeRunIdsWithASingleRegistrationPageUsingRunTokens()
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
        const string earlier = "1.2.0-beta.3.pr.755.run.r0000000003000000000.attempt.1.artifact.200";
        const string later = "1.2.0-beta.3.pr.755.run.r0000000010000000000.attempt.1.artifact.200";

        using (var versions = JsonDocument.Parse(await CatalogTestSynchronization.GetStringWhenSettledAsync(
            client, app.Services.GetRequiredService<PackageCatalog>(), "flat/acme.core/index.json")))
        {
            await Assert.That(versions.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(earlier);
            await Assert.That(versions.RootElement.GetProperty("versions")[1].GetString()).IsEqualTo(later);
        }
        using (var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.core/index.json")))
        {
            await Assert.That(registration.RootElement.GetProperty("count").GetInt32()).IsEqualTo(1);
            var page = registration.RootElement.GetProperty("items")[0];
            await Assert.That(page.GetProperty("lower").GetString()).IsEqualTo(earlier);
            await Assert.That(page.GetProperty("upper").GetString()).IsEqualTo(later);
            await Assert.That(page.GetProperty("items")[0].GetProperty("catalogEntry").GetProperty("version").GetString())
                .IsEqualTo(earlier);
            await Assert.That(page.GetProperty("items")[1].GetProperty("catalogEntry").GetProperty("version").GetString())
                .IsEqualTo(later);
        }
        using (var search = JsonDocument.Parse(await client.GetStringAsync("query?q=Acme.Core&prerelease=true&semVerLevel=2.0.0")))
            await Assert.That(search.RootElement.GetProperty("data")[0].GetProperty("version").GetString()).IsEqualTo(later);
    }

    [Test]
    public async Task NuGetProtocolResolvesExactLargeRunVersions()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        github.Runs = [
            new(3_000_000_000, 1, "old", "completed", "success", "pull_request", [new(755)]),
            new(10_000_000_000, 1, "new", "completed", "success", "pull_request", [new(755)])
        ];
        await using var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>(), [3_000_000_000]);
        const string earlier = "1.2.0-beta.3.pr.755.run.r0000000003000000000.attempt.1.artifact.200";
        const string later = "1.2.0-beta.3.pr.755.run.r0000000010000000000.attempt.1.artifact.200";

        var source = Repository.Factory.GetCoreV3(new PackageSource(app.Urls.Single() + "/v3/index.json")
        {
            AllowInsecureConnections = true
        });
        var resource = await source.GetResourceAsync<DependencyInfoResource>(CancellationToken.None);
        await Assert.That(resource!.GetType().Name).IsEqualTo("DependencyInfoResourceV3");
        using var context = new SourceCacheContext { NoCache = true };
        foreach (var (run, version) in new[] { (3_000_000_000L, earlier), (10_000_000_000L, later) })
        {
            var resolved = await resource.ResolvePackage(new PackageIdentity("Acme.Core", NuGetVersion.Parse(version)),
                NuGetFramework.ParseFolder("net8.0"), context, NuGet.Common.NullLogger.Instance, CancellationToken.None);
            await Assert.That(resolved is not null).IsTrue();
            await Assert.That(resolved!.Id).IsEqualTo("Acme.Core");
            await Assert.That(resolved.Version.ToNormalizedString()).IsEqualTo(version);
            var widgetVersion = PackageRewriter.VersionFor("2.0.0-rc.1", 755, run, 1, 200);
            var widget = await resource.ResolvePackage(new PackageIdentity("Acme.Widget", NuGetVersion.Parse(widgetVersion)),
                NuGetFramework.ParseFolder("net8.0"), context, NuGet.Common.NullLogger.Instance, CancellationToken.None);
            await Assert.That(widget is not null).IsTrue();
            await Assert.That(widget!.Version.ToNormalizedString()).IsEqualTo(widgetVersion);
            var dependency = widget.Dependencies.Single(x => x.Id.Equals("Acme.Core", StringComparison.OrdinalIgnoreCase));
            await Assert.That(dependency.VersionRange.ToNormalizedString())
                .IsEqualTo(VersionRange.Parse($"[{version}]").ToNormalizedString());
        }
        var available = (await resource.ResolvePackages("Acme.Core", NuGetFramework.ParseFolder("net8.0"),
            context, NuGet.Common.NullLogger.Instance, CancellationToken.None)).Select(x => x.Version).ToArray();
        await Assert.That(available.Length).IsEqualTo(2);
        var floating = VersionRange.Parse("1.2.0-beta.3.pr.755.run.r*").FindBestMatch(available);
        await Assert.That(floating!.ToNormalizedString()).IsEqualTo(later);
    }

    [Test]
    public async Task RegistrationKeepsASingleOrderedPageWhenComparersAgree()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        github.Runs = [
            new(9, 1, "old", "completed", "success", "pull_request", [new(755)]),
            new(10, 1, "new", "completed", "success", "pull_request", [new(755)])
        ];
        await using var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>(), [9]);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        using var registration = JsonDocument.Parse(await CatalogTestSynchronization.GetStringWhenSettledAsync(
            client, app.Services.GetRequiredService<PackageCatalog>(), "registration/acme.core/index.json"));
        await Assert.That(registration.RootElement.GetProperty("count").GetInt32()).IsEqualTo(1);
        var page = registration.RootElement.GetProperty("items")[0];
        await Assert.That(page.GetProperty("lower").GetString()).IsEqualTo("1.2.0-beta.3.pr.755.run.r0000000000000000009.attempt.1.artifact.200");
        await Assert.That(page.GetProperty("upper").GetString()).IsEqualTo("1.2.0-beta.3.pr.755.run.r0000000000000000010.attempt.1.artifact.200");
        await Assert.That(page.GetProperty("items").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task CatalogPreservesSemVerPrecedenceAcrossOriginalLabels()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var options = new ProxyOptions
        {
            Repository = "fixture/project",
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
        var runs = originals.Select(x => new RunArtifacts(x.Item1, 1, 755, "head", github.Packages, github.Version, WorkflowVerified: true)).ToList();
        var builds = originals.Select(x => new BuildEntry(x.Item1, 1, 755, "head", x.Item2,
            PackageRewriter.VersionFor(x.Item2, 755, x.Item1, 1, github.Packages.Id), github.Packages, github.Version)).ToList();
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, runs, SchemaVersion: 2), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(options.CacheDirectory, "catalog.json"),
            new CatalogFile(options.Repository, builds, CacheSchema.Version), CancellationToken.None);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var artifacts = new ArtifactCache(options, github);
        await using var catalog = new PackageCatalog(options, github, memory, artifacts, new PackageCache(options, artifacts));

        var actual = (await CatalogTestSynchronization.WhenSettledAsync(
            catalog, () => catalog.GetBuildsAsync(CancellationToken.None))).Select(x => x.OriginalVersion).ToArray();

        await Assert.That(string.Join(",", actual)).IsEqualTo("1.2.0-alpha.2,1.2.0-alpha.10,1.2.0-beta.3,1.2.0-rc.1");
    }

    [Test]
    public async Task BuildsEndpointDoesNotServeOutOfScopeFallbackWhenPinnedRunIsUnavailable()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cacheDirectory = Path.Combine(fixture.DirectoryPath, "cache");
        var options = new ProxyOptions { Repository = "fixture/project", CacheDirectory = cacheDirectory, PullRequests = [756], RunIds = [101] };
        Directory.CreateDirectory(cacheDirectory);
        var outOfScope = new BuildEntry(100, 1, 755, "old", "1.2.0-beta.3",
            PackageRewriter.VersionFor("1.2.0-beta.3", 755, 100, 1, github.Packages.Id), github.Packages, github.Version);
        var unavailablePackages = github.Packages with { Id = 300, Expired = true };
        var pinnedUnavailable = new BuildEntry(101, 1, 755, "new", "1.2.0-beta.3",
            PackageRewriter.VersionFor("1.2.0-beta.3", 755, 101, 1, unavailablePackages.Id), unavailablePackages, github.Version);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cacheDirectory, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, [
                new(outOfScope.RunId, outOfScope.Attempt, outOfScope.PullRequest, outOfScope.HeadSha, outOfScope.Packages, outOfScope.VersionArtifact),
                new(pinnedUnavailable.RunId, pinnedUnavailable.Attempt, pinnedUnavailable.PullRequest, pinnedUnavailable.HeadSha, pinnedUnavailable.Packages, pinnedUnavailable.VersionArtifact)
            ], SchemaVersion: 2), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cacheDirectory, "catalog.json"),
            new CatalogFile(options.Repository, [outOfScope, pinnedUnavailable], CacheSchema.Version), CancellationToken.None);

        await using var app = await StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>(), runIds: [101], pullRequests: [756]);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var path = await app.Services.GetRequiredService<PackageCache>().GetAsync("Acme.Core", outOfScope, CancellationToken.None);
        await Assert.That(File.Exists(path)).IsTrue();

        using (var builds = JsonDocument.Parse(await client.GetStringAsync("builds")))
            await Assert.That(builds.RootElement.GetArrayLength()).IsEqualTo(0);
        using (var packages = await client.GetAsync("flat/acme.core/index.json"))
            await Assert.That(packages.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NotFound);

        File.Copy(Path.Combine(cacheDirectory, "artifacts", "200.zip"), Path.Combine(cacheDirectory, "artifacts", "300.zip"));

        using (var builds = JsonDocument.Parse(await client.GetStringAsync("builds")))
        {
            var buildIds = builds.RootElement.EnumerateArray().Select(x => x.GetProperty("runId").GetInt64()).ToArray();
            await Assert.That(string.Join(",", buildIds)).IsEqualTo("101");
        }
        using (var packages = JsonDocument.Parse(await client.GetStringAsync("flat/acme.core/index.json")))
            await Assert.That(packages.RootElement.GetProperty("versions")[0].GetString()).IsEqualTo(pinnedUnavailable.Version);
    }

    internal static async Task<WebApplication> StartFeedAsync(PackageRewriteFixture fixture, FixtureGitHub github,
        CollectingLogger<PackageCache> logger, long[]? runIds = null, CollectingLogger<PackageCatalog>? catalogLogger = null,
        int[]? pullRequests = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var configuration = new Dictionary<string, string?>
        {
            ["Proxy:Repository"] = "fixture/project",
            ["Proxy:CacheDirectory"] = Path.Combine(fixture.DirectoryPath, "cache")
        };
        var configuredPullRequests = pullRequests ?? [755];
        for (var i = 0; i < configuredPullRequests.Length; i++)
            configuration[$"Proxy:PullRequests:{i}"] = configuredPullRequests[i].ToString();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(github);
        builder.Services.AddSingleton<ILogger<PackageCache>>(logger);
        if (catalogLogger is not null) builder.Services.AddSingleton<ILogger<PackageCatalog>>(catalogLogger);
        var app = builder.Build();
        app.Services.GetRequiredService<ProxyOptions>().RunIds = runIds ?? [99];
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync();
        app.Services.GetRequiredService<ProxyOptions>().BaseUrl = app.Urls.Single();
        return app;
    }
}
