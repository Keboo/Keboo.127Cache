using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache.Tests;

public class PackageIntegrityFeedTests
{
    private const string HealthyVersion = "1.2.0-beta.3.pr.755.run.100.attempt.1.artifact.200";
    private const string OtherVersion = "1.2.0-beta.3.pr.755.run.99.attempt.1.artifact.300";

    [Test]
    [Arguments("id")]
    [Arguments("version")]
    [Arguments("metadata")]
    [Arguments("dependency-range")]
    public async Task MalformedNuspecDoesNotBlockAnIndependentHealthyBuild(string duplicate)
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        github.SeedRawCache(cache);
        github.FailDownloads = true;

        using var malformedFixture = new PackageRewriteFixture();
        var source = duplicate == "dependency-range"
            ? malformedFixture.CreatePackage("Acme.Core", "1.2.0-beta.3",
                """<dependencies><dependency id="External.Invalid" version="not a valid range" /></dependencies>""")
            : malformedFixture.CreatePackage("Broken.Package", "1.0.0");
        if (duplicate != "dependency-range") DuplicateManifestElement(source, duplicate);
        var artifactPath = Path.Combine(cache, "artifacts", "300.zip");
        using (var archive = ZipFile.Open(artifactPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(source, Path.GetFileName(source));
        var malformedArtifact = new Artifact(300, "packages", true,
            "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath))).ToLowerInvariant());
        await SeedBuildsAsync(cache, github, malformedArtifact, CacheSchema.Version);

        var logger = new CollectingLogger<PackageCatalog>();
        await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
            new CollectingLogger<PackageCache>(), catalogLogger: logger);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var versions = await VersionsAsync(client, "acme.core");
        await Assert.That(string.Join(",", versions)).IsEqualTo(HealthyVersion);
        using var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.core/index.json"));
        await Assert.That(registration.RootElement.GetProperty("items")[0].GetProperty("items")[0]
            .GetProperty("catalogEntry").GetProperty("version").GetString()).IsEqualTo(HealthyVersion);
        var bytes = await client.GetByteArrayAsync($"flat/acme.core/{HealthyVersion}/acme.core.{HealthyVersion}.nupkg");
        using var reader = new PackageArchiveReader(new MemoryStream(bytes));
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(HealthyVersion);
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("99", StringComparison.Ordinal)
            && x.Message.Contains("300", StringComparison.Ordinal)
            && x.Exception is InvalidDataException
            && x.Exception.Message.Contains(duplicate == "dependency-range" ? "dependency" : duplicate,
                StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PartialLegacyInventoryIsIsolatedUntilTheCompleteRawArtifactIsRestored(int schema)
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        github.SeedRawCache(cache);
        github.FailDownloads = true;
        var missingArtifact = github.Packages with { Id = 300, Expired = true };
        await SeedBuildsAsync(cache, github, missingArtifact, schema);
        var catalogPath = Path.Combine(cache, "catalog.json");
        var originalCatalog = await File.ReadAllTextAsync(catalogPath);

        // A prior migration wrote one correct v2 package but silently omitted two others.
        var legacyDirectory = Path.Combine(cache, "packages", "discovered", "v2", "755.99.1.300");
        Directory.CreateDirectory(legacyDirectory);
        var legacyIndex = Path.Combine(legacyDirectory, "packages.json");
        await JsonSettings.WriteAtomicAsync(legacyIndex,
            new[] { new CachedPackage("Acme.Core", "1.2.0-beta.3", OtherVersion) }, CancellationToken.None);
        var legacyPackage = Path.Combine(legacyDirectory, $"acme.core.{OtherVersion}.nupkg");
        await PackageRewriter.RewriteAsync(Path.Combine(fixture.DirectoryPath, "Acme.Core.nupkg"), legacyPackage,
            "Acme.Core", "1.2.0-beta.3", OtherVersion);
        var indexBytes = await File.ReadAllBytesAsync(legacyIndex);
        var packageBytes = await File.ReadAllBytesAsync(legacyPackage);

        for (var restart = 0; restart < 2; restart++)
        {
            var logger = new CollectingLogger<PackageCatalog>();
            await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
                new CollectingLogger<PackageCache>(), catalogLogger: logger);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core"))).IsEqualTo(HealthyVersion);
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.widget")))
                .IsEqualTo("2.0.0-rc.1.pr.755.run.100.attempt.1.artifact.200");
            await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(originalCatalog);
            await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
                && x.Message.Contains("99", StringComparison.Ordinal)
                && x.Message.Contains("300", StringComparison.Ordinal))).IsTrue();
        }

        File.Copy(Path.Combine(cache, "artifacts", "200.zip"), Path.Combine(cache, "artifacts", "300.zip"));
        await using (var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>()))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core")))
                .IsEqualTo($"{OtherVersion},{HealthyVersion}");
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.widget")))
                .IsEqualTo("2.0.0-rc.1.pr.755.run.99.attempt.1.artifact.300,2.0.0-rc.1.pr.755.run.100.attempt.1.artifact.200");
            await Assert.That((await VersionsAsync(client, "acme.empty")).Length).IsEqualTo(2);
            var bytes = await client.GetByteArrayAsync($"flat/acme.core/{OtherVersion}/acme.core.{OtherVersion}.nupkg");
            using var reader = new PackageArchiveReader(new MemoryStream(bytes));
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(OtherVersion);
            using var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.widget/index.json"));
            var leaves = registration.RootElement.GetProperty("items")[0].GetProperty("items").EnumerateArray().ToArray();
            var recovered = leaves.Single(x => x.GetProperty("catalogEntry").GetProperty("version").GetString()!
                .Contains(".run.99.", StringComparison.Ordinal));
            var dependency = recovered.GetProperty("catalogEntry").GetProperty("dependencyGroups")[0]
                .GetProperty("dependencies")[0];
            await Assert.That(dependency.GetProperty("range").GetString())
                .IsEqualTo(VersionRange.Parse($"[{OtherVersion}]").ToNormalizedString());
        }

        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(catalogPath), JsonSettings.Options)!;
        await Assert.That(saved.SchemaVersion).IsEqualTo(CacheSchema.Version);
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(legacyIndex))).IsEqualTo(Convert.ToHexString(indexBytes));
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(legacyPackage))).IsEqualTo(Convert.ToHexString(packageBytes));
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    public async Task MigrationRejectsASelfConsistentPartialCurrentInventory()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        github.SeedRawCache(cache);
        github.FailDownloads = true;
        File.Copy(Path.Combine(cache, "artifacts", "200.zip"), Path.Combine(cache, "artifacts", "300.zip"));
        await SeedBuildsAsync(cache, github, github.Packages with { Id = 300, Expired = true }, CacheSchema.Version);
        await using (var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>()))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That((await VersionsAsync(client, "acme.core")).Length).IsEqualTo(2);
        }

        var directory = Path.Combine(cache, "packages", "discovered", $"v{CacheSchema.Version}", "755.99.1.300");
        var inventoryPath = Path.Combine(directory, "packages.json");
        var proofPath = Path.Combine(directory, "integrity.json");
        var originalInventory = await File.ReadAllBytesAsync(inventoryPath);
        var originalProof = await File.ReadAllBytesAsync(proofPath);
        var partial = JsonNode.Parse(originalInventory)!.AsArray();
        while (partial.Count > 1) partial.RemoveAt(partial.Count - 1);
        await File.WriteAllTextAsync(inventoryPath, partial.ToJsonString(JsonSettings.Options));
        var proof = JsonNode.Parse(originalProof)!.AsObject();
        proof["inventorySha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(inventoryPath)));
        var digests = proof["packages"]!.AsArray();
        while (digests.Count > 1) digests.RemoveAt(digests.Count - 1);
        await File.WriteAllTextAsync(proofPath, proof.ToJsonString(JsonSettings.Options));
        var partialInventory = await File.ReadAllBytesAsync(inventoryPath);
        var partialProof = await File.ReadAllBytesAsync(proofPath);
        var catalogPath = Path.Combine(cache, "catalog.json");
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
        catalog["schemaVersion"] = 2;
        await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString(JsonSettings.Options));
        var legacyCatalog = await File.ReadAllTextAsync(catalogPath);

        var logger = new CollectingLogger<PackageCatalog>();
        await using (var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
            new CollectingLogger<PackageCache>(), catalogLogger: logger))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core"))).IsEqualTo(HealthyVersion);
            await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(legacyCatalog);
            await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
                && x.Message.Contains("99", StringComparison.Ordinal)
                && x.Message.Contains("300", StringComparison.Ordinal)
                && x.Exception?.Message.Contains("inventory", StringComparison.OrdinalIgnoreCase) == true)).IsTrue();
        }
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(inventoryPath))).IsEqualTo(Convert.ToHexString(partialInventory));
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(proofPath))).IsEqualTo(Convert.ToHexString(partialProof));

        await File.WriteAllBytesAsync(inventoryPath, originalInventory);
        await File.WriteAllBytesAsync(proofPath, originalProof);
        await using (var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>()))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core")))
                .IsEqualTo($"{OtherVersion},{HealthyVersion}");
        }
        var saved = JsonSerializer.Deserialize<CatalogFile>(await File.ReadAllTextAsync(catalogPath), JsonSettings.Options)!;
        await Assert.That(saved.SchemaVersion).IsEqualTo(CacheSchema.Version);
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    [Test]
    [Arguments("identity")]
    [Arguments("different-id")]
    [Arguments("archive")]
    [Arguments("payload")]
    [Arguments("partial-inventory")]
    [Arguments("missing-proof")]
    public async Task CorruptCurrentCacheIsIsolatedAcrossRestartAndRecoversFromRestoredBytes(string corruption)
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var cache = Path.Combine(fixture.DirectoryPath, "cache");
        github.SeedRawCache(cache);
        github.FailDownloads = true;
        File.Copy(Path.Combine(cache, "artifacts", "200.zip"), Path.Combine(cache, "artifacts", "300.zip"));
        var artifact = github.Packages with { Id = 300, Expired = true };
        var (other, healthy) = await SeedBuildsAsync(cache, github, artifact, CacheSchema.Version);

        string packagePath;
        byte[] healthyBytes;
        await using (var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>()))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core")))
                .IsEqualTo($"{OtherVersion},{HealthyVersion}");
            var packages = app.Services.GetRequiredService<PackageCache>();
            packagePath = await packages.GetAsync("Acme.Core", other, CancellationToken.None);
            healthyBytes = await File.ReadAllBytesAsync(await packages.GetAsync("Acme.Core", healthy, CancellationToken.None));
        }
        var inventoryPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "packages.json");
        var proofPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "integrity.json");
        var originalPackage = await File.ReadAllBytesAsync(packagePath);
        var originalInventory = await File.ReadAllBytesAsync(inventoryPath);
        var originalProof = await File.ReadAllBytesAsync(proofPath);
        if (corruption == "identity") await File.WriteAllBytesAsync(packagePath, healthyBytes);
        else if (corruption == "different-id")
            File.Copy(fixture.CreatePackage("Acme.Other", OtherVersion), packagePath, overwrite: true);
        else if (corruption == "archive") await File.WriteAllTextAsync(packagePath, "not a NuGet package");
        else if (corruption == "missing-proof") File.Delete(proofPath);
        else if (corruption == "payload")
        {
            using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
            archive.GetEntry("runtimes/linux-x64/native/tool.txt")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("runtimes/linux-x64/native/tool.txt").Open());
            await writer.WriteAsync("corrupt payload with unchanged package identity");
        }
        else
        {
            var inventory = JsonNode.Parse(originalInventory)!.AsArray();
            while (inventory.Count > 1) inventory.RemoveAt(inventory.Count - 1);
            await File.WriteAllTextAsync(inventoryPath, inventory.ToJsonString(JsonSettings.Options));
        }
        var corruptPackage = await File.ReadAllBytesAsync(packagePath);
        var corruptInventory = await File.ReadAllBytesAsync(inventoryPath);
        var expectedReason = corruption switch
        {
            "identity" or "different-id" => "mismatched identity",
            "payload" => "SHA256",
            "partial-inventory" => "inventory integrity",
            "missing-proof" => "no integrity record",
            _ => null
        };

        for (var restart = 0; restart < 2; restart++)
        {
            var logger = new CollectingLogger<PackageCatalog>();
            await using var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github,
                new CollectingLogger<PackageCache>(), catalogLogger: logger);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core"))).IsEqualTo(HealthyVersion);
            using var registration = JsonDocument.Parse(await client.GetStringAsync("registration/acme.core/index.json"));
            await Assert.That(registration.RootElement.GetProperty("items")[0].GetProperty("items").GetArrayLength()).IsEqualTo(1);
            var bytes = await client.GetByteArrayAsync($"flat/acme.core/{HealthyVersion}/acme.core.{HealthyVersion}.nupkg");
            using var reader = new PackageArchiveReader(new MemoryStream(bytes));
            await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(HealthyVersion);
            await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
                && x.Message.Contains("99", StringComparison.Ordinal)
                && x.Message.Contains("300", StringComparison.Ordinal)
                && x.Exception is InvalidDataException
                && (expectedReason is null || x.Exception.Message.Contains(expectedReason, StringComparison.OrdinalIgnoreCase)))).IsTrue();
        }
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(packagePath))).IsEqualTo(Convert.ToHexString(corruptPackage));
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(inventoryPath))).IsEqualTo(Convert.ToHexString(corruptInventory));
        if (corruption == "missing-proof") await Assert.That(File.Exists(proofPath)).IsFalse();
        else await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(proofPath))).IsEqualTo(Convert.ToHexString(originalProof));

        await File.WriteAllBytesAsync(packagePath, originalPackage);
        await File.WriteAllBytesAsync(inventoryPath, originalInventory);
        await File.WriteAllBytesAsync(proofPath, originalProof);
        await using (var app = await PackageRewriteFeedTests.StartFeedAsync(fixture, github, new CollectingLogger<PackageCache>()))
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            await Assert.That(string.Join(",", await VersionsAsync(client, "acme.core")))
                .IsEqualTo($"{OtherVersion},{HealthyVersion}");
            await Assert.That((await VersionsAsync(client, "acme.widget")).Length).IsEqualTo(2);
        }
        await Assert.That(github.Downloads).IsEqualTo(0);
    }

    private static async Task<(BuildEntry Other, BuildEntry Healthy)> SeedBuildsAsync(
        string cache, FixtureGitHub github, Artifact otherArtifact, int schema)
    {
        var options = new ProxyOptions { CacheDirectory = cache, PullRequests = [755] };
        var other = new BuildEntry(99, 1, 755, "other", "1.2.0-beta.3", OtherVersion, otherArtifact, github.Version);
        var healthy = new BuildEntry(100, 1, 755, "healthy", "1.2.0-beta.3", HealthyVersion, github.Packages, github.Version);
        if (schema == 1)
        {
            other = other with { Version = "1.2.0-pr.755.run.99.attempt.1.artifact.300" };
            healthy = healthy with { Version = "1.2.0-pr.755.run.100.attempt.1.artifact.200" };
        }
        var builds = new[] { other, healthy };
        var runs = builds.Select(x => new RunArtifacts(x.RunId, x.Attempt, x.PullRequest, x.HeadSha, x.Packages, x.VersionArtifact)).ToList();
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "runs.json"),
            new RunArtifactsFile(options.Repository, options.Workflow, runs), CancellationToken.None);
        await JsonSettings.WriteAtomicAsync(Path.Combine(cache, "catalog.json"),
            new CatalogFile(options.Repository, builds.ToList(), schema), CancellationToken.None);
        return (other, healthy);
    }

    private static async Task<string[]> VersionsAsync(HttpClient client, string id)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync($"flat/{id}/index.json"));
        return document.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()!).ToArray();
    }

    private static void DuplicateManifestElement(string path, string element)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("Broken.Package.nuspec")!;
        XDocument manifest;
        using (var input = entry.Open()) manifest = XDocument.Load(input);
        var metadata = manifest.Root!.Elements().Single(x => x.Name.LocalName == "metadata");
        if (element == "metadata") manifest.Root.Add(new XElement(metadata));
        else metadata.Add(new XElement(metadata.Elements().Single(x => x.Name.LocalName == element)));
        entry.Delete();
        using var output = archive.CreateEntry("Broken.Package.nuspec").Open();
        manifest.Save(output);
    }
}
