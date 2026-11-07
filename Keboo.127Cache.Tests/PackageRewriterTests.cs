using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache.Tests;

public class PackageRewriterTests
{
    [Test]
    [Arguments("1.2.0", "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.0+sha", "1.2.0-pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.0-beta.3", "1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.0-beta.3+sha", "1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.0-RC.1+sha", "1.2.0-RC.1.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.0-alpha-beta", "1.2.0-alpha-beta.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.3.67-gabcdef0+metadata", "1.3.67-gabcdef0.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.3.4", "1.2.3.4-pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.3.4-rc.1+sha", "1.2.3.4-rc.1.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    [Arguments("1.2.3.0-rc.1", "1.2.3-rc.1.pr.755.run.r0000000000000000100.attempt.1.artifact.200")]
    public async Task VersionGoldenCasesAreNormalizedAndParsable(string original, string expected)
    {
        var actual = PackageRewriter.VersionFor(original, 755, 100, 1, 200);

        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(NuGetVersion.Parse(actual).ToNormalizedString()).IsEqualTo(expected);
        await Assert.That(VersionRange.Parse($"[{actual}]").Satisfies(NuGetVersion.Parse(actual))).IsTrue();
        if (!actual.StartsWith("1.2.3.4-", StringComparison.Ordinal))
        {
            const string semVer2 = @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*$";
            await Assert.That(Regex.IsMatch(actual, semVer2)).IsTrue();
        }
    }

    [Test]
    public async Task VersionsAreDistinctAndSortByOrdinaryRunNumbers()
    {
        var versions = new[]
        {
            PackageRewriter.VersionFor("1.2.0-beta.3", 755, 9, 1, 200),
            PackageRewriter.VersionFor("1.2.0-beta.3", 755, 10, 1, 200),
            PackageRewriter.VersionFor("1.2.0-beta.3", 755, 10, 2, 200),
            PackageRewriter.VersionFor("1.2.0-beta.3", 755, 10, 2, 201),
            PackageRewriter.VersionFor("1.2.0-beta.3", 756, 10, 2, 201)
        };

        await Assert.That(versions.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(versions.Length);
        await Assert.That(VersionComparer.VersionRelease.Compare(NuGetVersion.Parse(versions[0]), NuGetVersion.Parse(versions[1])) < 0).IsTrue();
        await Assert.That(VersionComparer.VersionRelease.Compare(NuGetVersion.Parse(versions[1]), NuGetVersion.Parse(versions[2])) < 0).IsTrue();
    }

    [Test]
    public async Task RewritesAllCorePropertiesAndPinsEveryGroupWithoutChangingOtherEntries()
    {
        using var fixture = new PackageRewriteFixture();
        var source = fixture.CreatePackage("Acme.Widget", "2.0.0-rc.1", PackageRewriteFixture.WidgetDependencies,
            coreIdentifier: "outdated-id", additionalCoreProperties: true);
        var destination = Path.Combine(fixture.DirectoryPath, "rewritten.nupkg");
        var version = PackageRewriter.VersionFor("2.0.0-rc.1", 755, 100, 1, 200);
        var dependencyVersion = PackageRewriter.VersionFor("1.2.0-beta.3+sha", 755, 100, 1, 200);
        var caseSensitiveLookup = new Dictionary<string, string> { ["Acme.Core"] = dependencyVersion };

        await PackageRewriter.RewriteAsync(source, destination, "acme.widget", "2.0.0-rc.1", version, caseSensitiveLookup);

        using var original = ZipFile.OpenRead(source);
        using var rewritten = ZipFile.OpenRead(destination);
        await Assert.That(rewritten.Entries.Count).IsEqualTo(original.Entries.Count);
        foreach (var entry in original.Entries)
        {
            var actual = rewritten.GetEntry(entry.FullName);
            await Assert.That(actual is not null).IsTrue();
            await Assert.That(actual!.ExternalAttributes).IsEqualTo(entry.ExternalAttributes);
            await Assert.That(actual.LastWriteTime).IsEqualTo(entry.LastWriteTime);
            if (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase)) continue;
            using var input = entry.Open();
            using var output = actual.Open();
            await Assert.That(Convert.ToHexString(SHA256.HashData(output))).IsEqualTo(Convert.ToHexString(SHA256.HashData(input)));
        }

        var manifest = PackageRewriteFixture.ReadXml(rewritten, "Acme.Widget.nuspec");
        var metadata = manifest.Root!.Elements().Single(x => x.Name.LocalName == "metadata");
        await Assert.That(metadata.Elements().Single(x => x.Name.LocalName == "id").Value).IsEqualTo("Acme.Widget");
        await Assert.That(metadata.Elements().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(version);
        var dependencies = metadata.Descendants().Where(x => x.Name.LocalName == "dependency").ToArray();
        await Assert.That(dependencies.Length).IsEqualTo(4);
        await Assert.That((string?)dependencies[0].Attribute("version")).IsEqualTo($"[{dependencyVersion}]");
        await Assert.That((string?)dependencies[2].Attribute("version")).IsEqualTo($"[{dependencyVersion}]");
        await Assert.That((string?)dependencies[0].Attribute("include")).IsEqualTo("Compile");
        await Assert.That((string?)dependencies[0].Attribute("exclude")).IsEqualTo("Runtime");
        await Assert.That((string?)dependencies[2].Attribute("exclude")).IsEqualTo("Analyzers");
        await Assert.That((string?)dependencies[1].Attribute("version")).IsEqualTo("[3.1.0,4.0.0)");
        await Assert.That((string?)dependencies[1].Attribute("exclude")).IsEqualTo("Build");
        await Assert.That((string?)dependencies[3].Attribute("version")).IsEqualTo("(,5.0.0]");
        foreach (var path in new[] { PackageRewriteFixture.CorePropertiesPath, "package/services/metadata/core-properties/extra.psmdcp" })
        {
            var properties = PackageRewriteFixture.ReadXml(rewritten, path);
            XNamespace core = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            await Assert.That(properties.Root!.Element(core + "version")!.Value).IsEqualTo(version);
            await Assert.That(properties.Root.Element(dc + "identifier")!.Value).IsEqualTo("Acme.Widget");
            await Assert.That(properties.Root.Element(dc + "creator")!.Value).IsEqualTo("Example Author");
        }

        using var package = new PackageArchiveReader(destination);
        await Assert.That(package.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
        await Assert.That(package.NuspecReader.GetDependencyGroups().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task UngroupedDependenciesAreAlsoPinned()
    {
        using var fixture = new PackageRewriteFixture();
        var source = fixture.CreatePackage("Acme.Flat", "1.0.0", """
            <dependencies><dependency id="aCmE.cOrE" version="(,2.0.0]" /><dependency id="External" version="2.0.0" /></dependencies>
            """);
        var destination = Path.Combine(fixture.DirectoryPath, "flat-output.nupkg");
        var version = PackageRewriter.VersionFor("1.0.0", 755, 100, 1, 200);
        var dependencyVersion = PackageRewriter.VersionFor("1.2.0-beta.3", 755, 100, 1, 200);

        await PackageRewriter.RewriteAsync(source, destination, "Acme.Flat", "1.0.0", version,
            new Dictionary<string, string> { ["ACME.CORE"] = dependencyVersion });

        using var archive = ZipFile.OpenRead(destination);
        var dependencies = PackageRewriteFixture.ReadXml(archive, "Acme.Flat.nuspec").Descendants()
            .Where(x => x.Name.LocalName == "dependency").ToArray();
        await Assert.That((string?)dependencies[0].Attribute("version")).IsEqualTo($"[{dependencyVersion}]");
        await Assert.That((string?)dependencies[1].Attribute("version")).IsEqualTo("2.0.0");
    }

    [Test]
    public async Task PackageWithoutDependenciesKeepsFourPartIdentityAndPayload()
    {
        using var fixture = new PackageRewriteFixture();
        var source = fixture.CreatePackage("Acme.Empty", "1.2.3.4+build");
        var destination = Path.Combine(fixture.DirectoryPath, "empty-output.nupkg");
        var version = PackageRewriter.VersionFor("1.2.3.4+build", 755, 100, 1, 200);

        await PackageRewriter.RewriteAsync(source, destination, "Acme.Empty", "1.2.3.4", version,
            new Dictionary<string, string> { ["Other"] = "1.0.0-pr.1" });

        using var archive = ZipFile.OpenRead(destination);
        var manifest = PackageRewriteFixture.ReadXml(archive, "Acme.Empty.nuspec");
        await Assert.That(manifest.Descendants().Any(x => x.Name.LocalName == "dependencies")).IsFalse();
        await Assert.That(manifest.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(version);
        var properties = PackageRewriteFixture.ReadXml(archive, PackageRewriteFixture.CorePropertiesPath);
        await Assert.That(properties.Descendants().Single(x => x.Name.LocalName == "version").Value).IsEqualTo(version);
        await Assert.That(archive.GetEntry("runtimes/linux-x64/native/tool.txt") is not null).IsTrue();
        using var package = new PackageArchiveReader(destination);
        await Assert.That(package.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
    }

    [Test]
    public async Task MissingCoreVersionIsAddedWithoutChangingOtherProperties()
    {
        using var fixture = new PackageRewriteFixture();
        var source = fixture.CreatePackage("Acme.Core", "1.2.0-beta.3", omitCoreVersion: true);
        var destination = Path.Combine(fixture.DirectoryPath, "missing-version-output.nupkg");
        var version = PackageRewriter.VersionFor("1.2.0-beta.3", 755, 100, 1, 200);

        await PackageRewriter.RewriteAsync(source, destination, "Acme.Core", "1.2.0-beta.3", version);

        using var archive = ZipFile.OpenRead(destination);
        var properties = PackageRewriteFixture.ReadXml(archive, PackageRewriteFixture.CorePropertiesPath);
        XNamespace core = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        await Assert.That(properties.Root!.Element(core + "version")!.Value).IsEqualTo(version);
        await Assert.That(properties.Root.Element(dc + "identifier")!.Value).IsEqualTo("Acme.Core");
        await Assert.That(properties.Root.Element(dc + "creator")!.Value).IsEqualTo("Example Author");
    }

    [Test]
    public async Task MismatchedIdentityIsStillRejected()
    {
        using var fixture = new PackageRewriteFixture();
        var source = fixture.CreatePackage("Acme.Core", "1.2.0-beta.3");
        var destination = Path.Combine(fixture.DirectoryPath, "wrong-output.nupkg");

        await Assert.That(async () => await PackageRewriter.RewriteAsync(source, destination, "Other", "1.2.0-beta.3",
            "1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.artifact.200")).ThrowsExactly<InvalidDataException>();
        await Assert.That(File.Exists(destination)).IsFalse();
    }
}
