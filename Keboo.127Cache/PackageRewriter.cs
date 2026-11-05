using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache;

public static class PackageRewriter
{
    private static readonly XNamespace CorePropertiesNamespace = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    internal static readonly IComparer<NuGetVersion> VersionOrder = Comparer<NuGetVersion>.Create(CompareVersions);

    public static string VersionFor(string original, int pr, long run, int attempt, long artifact)
    {
        var version = NuGetVersion.Parse(original);
        var numeric = version.Version.Revision > 0 ? version.Version.ToString(4) : version.Version.ToString(3);
        var prefix = version.IsPrerelease ? $"-{version.Release}.pr" : "-pr";
        return FormattableString.Invariant($"{numeric}{prefix}.{pr}.run.{run}.attempt.{attempt}.artifact.{artifact}");
    }

    public static void Rewrite(string source, string destination, string id, string originalVersion, string version,
        IReadOnlyDictionary<string, string>? packageVersions = null) =>
        RewriteAsync(source, destination, id, originalVersion, version, packageVersions).GetAwaiter().GetResult();

    public static async Task RewriteAsync(string source, string destination, string id, string originalVersion, string version,
        IReadOnlyDictionary<string, string>? packageVersions = null, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var sourceFile = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var sourceFileScope = sourceFile.ConfigureAwait(false);
        using var archive = new ZipArchive(sourceFile, ZipArchiveMode.Read);
        var hasSignature = archive.Entries.Any(IsSignature);
        var manifests = archive.Entries.Where(x => !x.FullName.Contains('/') && x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("Expected exactly one root nuspec.");
        XDocument manifest;
        using (var stream = manifests[0].Open())
            manifest = await XDocument.LoadAsync(stream, LoadOptions.PreserveWhitespace, cancellationToken).ConfigureAwait(false);
        var (metadata, idElement, versionElement) = ManifestIdentity(manifest);
        if (!idElement.Value.Equals(id, StringComparison.OrdinalIgnoreCase)
            || NuGetVersion.Parse(versionElement.Value) != NuGetVersion.Parse(originalVersion))
            throw new InvalidDataException($"Package identity does not match expected {id} {originalVersion}.");
        versionElement.Value = version;
        if (packageVersions is not null)
        {
            var versions = new Dictionary<string, string>(packageVersions, StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in metadata.Elements().Where(x => x.Name.LocalName == "dependencies")
                .Descendants().Where(x => x.Name.LocalName == "dependency"))
            {
                if (dependency.Attribute("id") is { } dependencyId && versions.TryGetValue(dependencyId.Value, out var dependencyVersion))
                    dependency.SetAttributeValue("version", $"[{dependencyVersion}]");
            }
        }

        var destinationFile = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (destinationFile.ConfigureAwait(false))
        using (var target = new ZipArchive(destinationFile, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSignature(entry)) continue;
                var rewritten = target.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                rewritten.LastWriteTime = entry.LastWriteTime;
                rewritten.ExternalAttributes = entry.ExternalAttributes;
                using var output = rewritten.Open();
                if (entry == manifests[0])
                {
                    await manifest.SaveAsync(output, SaveOptions.DisableFormatting, cancellationToken).ConfigureAwait(false);
                }
                else if (IsCoreProperties(entry))
                {
                    XDocument properties;
                    using (var input = entry.Open())
                        properties = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, cancellationToken).ConfigureAwait(false);
                    var root = properties.Root;
                    if (root?.Name != CorePropertiesNamespace + "coreProperties")
                        throw new InvalidDataException($"Invalid package core properties in {entry.FullName}.");
                    var versions = root.Elements(CorePropertiesNamespace + "version").ToArray();
                    if (versions.Length == 0) root.Add(new XElement(CorePropertiesNamespace + "version", version));
                    else foreach (var property in versions) property.Value = version;
                    foreach (var property in root.Elements(DublinCoreNamespace + "identifier"))
                        property.Value = idElement.Value;
                    await properties.SaveAsync(output, SaveOptions.DisableFormatting, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var input = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        if (hasSignature)
            logger?.LogInformation("Removed NuGet signature from rewritten package {PackageId} {OriginalVersion}.", id, originalVersion);
    }

    internal static (XElement Metadata, XElement Id, XElement Version) ManifestIdentity(XDocument manifest)
    {
        var root = manifest.Root ?? throw new InvalidDataException("Nuspec has no root element.");
        var metadata = RequiredElement(root, "metadata");
        return (metadata, RequiredElement(metadata, "id"), RequiredElement(metadata, "version"));
    }

    private static XElement RequiredElement(XElement parent, string name)
    {
        var elements = parent.Elements().Where(x => x.Name.LocalName == name).ToArray();
        return elements.Length == 1 ? elements[0]
            : throw new InvalidDataException($"Nuspec must contain exactly one {name} element; found {elements.Length}.");
    }

    private static bool IsSignature(ZipArchiveEntry entry) =>
        entry.FullName.Equals(".signature.p7s", StringComparison.Ordinal);

    private static bool IsCoreProperties(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith("package/services/metadata/core-properties/", StringComparison.OrdinalIgnoreCase)
        && entry.FullName.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase);

    private static int CompareVersions(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        var numeric = left.Version.CompareTo(right.Version);
        if (numeric != 0) return numeric;
        if (!left.IsPrerelease) return right.IsPrerelease ? 1 : 0;
        if (!right.IsPrerelease) return -1;

        var leftLabels = left.ReleaseLabels.ToArray();
        var rightLabels = right.ReleaseLabels.ToArray();
        for (var i = 0; i < Math.Min(leftLabels.Length, rightLabels.Length); i++)
        {
            var a = leftLabels[i];
            var b = rightLabels[i];
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            int comparison;
            if (aNumeric && bNumeric)
            {
                // NuGet's comparer falls back to lexical ordering for large numeric labels.
                comparison = a.Length.CompareTo(b.Length);
                if (comparison == 0) comparison = string.CompareOrdinal(a, b);
            }
            else if (aNumeric != bNumeric) comparison = aNumeric ? -1 : 1;
            else comparison = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return leftLabels.Length.CompareTo(rightLabels.Length);
    }

    public static async Task VerifyDigestAsync(string path, Artifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.Digest is null || !artifact.Digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException($"Artifact {artifact.Id} has no supported SHA256 digest.");
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!actual.Equals(artifact.Digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact {artifact.Id} failed SHA256 verification.");
    }

    public static byte[] ReadManifest(string path)
    {
        using var reader = new PackageArchiveReader(path);
        using var stream = reader.GetNuspec();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
