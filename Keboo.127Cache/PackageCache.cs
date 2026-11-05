using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Keboo._127Cache;

public sealed record CachedPackage(string Id, string OriginalVersion, string Version);

public sealed class PackageCache(ProxyOptions options, ArtifactCache artifacts, ILogger<PackageCache>? logger = null)
{
    private sealed record PackageDigest(string Id, string Sha256);
    // The seal detects corruption; migration separately verifies completeness against the raw artifact.
    private sealed record InventorySeal(int SchemaVersion, long ArtifactId, string? ArtifactDigest,
        string InventorySha256, PackageDigest[] Packages);

    private readonly SemaphoreSlim _gate = new(1);
    private readonly ILogger<PackageCache> _logger = logger ?? NullLogger<PackageCache>.Instance;

    private string DirectoryFor(BuildEntry build) => Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}");

    private static string FileNameFor(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()))) + ".nupkg";

    private static PackageIdentity ReadIdentity(PackageArchiveReader reader, long artifactId)
    {
        try
        {
            using var stream = reader.GetNuspec();
            PackageRewriter.ManifestIdentity(XDocument.Load(stream));
            return reader.GetIdentity();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException($"Invalid package identity in artifact {artifactId}: {exception.Message}", exception);
        }
    }

    private static void EnsureClientCacheSafe(CachedPackage package, BuildEntry build)
    {
        var clientFile = $"{package.Id.ToLowerInvariant()}.{package.Version.ToLowerInvariant()}.nupkg.sha512";
        var length = Encoding.UTF8.GetByteCount(clientFile);
        if (length > 255)
            throw new InvalidDataException($"Package {package.Id} {package.Version} in artifact {build.Packages.Id} requires a {length}-byte " +
                "NuGet client cache filename, exceeding the 255-byte component limit on common filesystems. " +
                "The approved public version cannot be restored safely and was not changed.");
    }

    private static void ValidateFeedMetadata(PackageArchiveReader reader, long artifactId)
    {
        try
        {
            using (var stream = reader.GetNuspec())
            {
                var (metadata, _, _) = PackageRewriter.ManifestIdentity(XDocument.Load(stream));
                foreach (var dependency in metadata.Elements().Where(x => x.Name.LocalName == "dependencies")
                    .Descendants().Where(x => x.Name.LocalName == "dependency"))
                {
                    var id = (string?)dependency.Attribute("id");
                    if (string.IsNullOrWhiteSpace(id) || !PackageIdValidator.IsValidPackageId(id))
                        throw new InvalidDataException("A dependency has an invalid or missing package ID.");
                    var range = (string?)dependency.Attribute("version");
                    if (!string.IsNullOrWhiteSpace(range) && !VersionRange.TryParse(range, out _))
                        throw new InvalidDataException($"Invalid dependency range '{range}' for {id}.");
                }
            }
            var nuspec = reader.NuspecReader;
            _ = nuspec.GetAuthors();
            _ = nuspec.GetDescription();
            _ = nuspec.GetPackageTypes().ToArray();
            foreach (var group in nuspec.GetDependencyGroups())
            {
                _ = group.TargetFramework.GetShortFolderName();
                foreach (var dependency in group.Packages)
                    _ = dependency.VersionRange.ToNormalizedString();
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException
            or InvalidDataException or PackagingException or System.Xml.XmlException)
        {
            throw new InvalidDataException($"Invalid dependency or package metadata in artifact {artifactId}: {exception.Message}", exception);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<(Dictionary<string, string> Originals, List<CachedPackage> Packages)> ReadArtifactAsync(
        BuildEntry build, string archivePath, string staging, CancellationToken cancellationToken)
    {
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<CachedPackage>();
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)))
        {
            // Artifact paths never become local filenames.
            var original = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".original");
            using (var input = entry.Open())
            {
                await using var output = new FileStream(original, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
            }
            using var reader = new PackageArchiveReader(original);
            var identity = ReadIdentity(reader, build.Packages.Id);
            if (!PackageIdValidator.IsValidPackageId(identity.Id))
                throw new InvalidDataException($"Invalid package ID in artifact {build.Packages.Id}.");
            if (!originals.TryAdd(identity.Id, original))
                throw new InvalidDataException($"Duplicate {identity.Id} in artifact {build.Packages.Id}.");
            var originalVersion = identity.Version.ToNormalizedString();
            packages.Add(new CachedPackage(identity.Id, originalVersion,
                PackageRewriter.VersionFor(originalVersion, build.PullRequest, build.RunId, build.Attempt, build.Packages.Id)));
        }
        if (packages.Count == 0)
            throw new InvalidDataException($"Artifact {build.Packages.Id} contains no NuGet packages.");
        return (originals, packages);
    }

    private static string? FindCachedPath(string directory, CachedPackage package)
    {
        var path = Path.Combine(directory, FileNameFor(package.Id));
        if (File.Exists(path)) return path;
        // A verified current-schema inventory may use the earlier public-identity filename.
        var legacyName = $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg";
        if (legacyName.Length > 255) return null;
        path = Path.Combine(directory, legacyName);
        return File.Exists(path) ? path : null;
    }

    public async Task<string> GetAsync(string id, BuildEntry build, CancellationToken cancellationToken)
    {
        var packages = await GetPackagesAsync(build, cancellationToken);
        var package = packages.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Package {id} is not present in artifact {build.Packages.Id}.");
        return FindCachedPath(DirectoryFor(build), package)
            ?? throw new InvalidDataException($"Cached package {id} is missing from artifact {build.Packages.Id}.");
    }

    internal async Task VerifyForMigrationAsync(BuildEntry build, CancellationToken cancellationToken)
    {
        var packages = await GetPackagesAsync(build, cancellationToken);
        var archivePath = await artifacts.GetAsync(build.Packages, cancellationToken);
        var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var (_, sourcePackages) = await ReadArtifactAsync(build, archivePath, staging, cancellationToken);
            var source = sourcePackages.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            if (source.Count != packages.Length || packages.Any(package =>
                !source.TryGetValue(package.Id, out var original)
                || original.OriginalVersion != package.OriginalVersion || original.Version != package.Version))
                throw new InvalidDataException($"Cached package inventory for artifact {build.Packages.Id} " +
                    "does not match the complete verified raw artifact.");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<CachedPackage[]> GetPackagesAsync(BuildEntry build, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(build);
        var index = Path.Combine(directory, "packages.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(index))
            {
                CachedPackage[] packages;
                var indexBytes = await File.ReadAllBytesAsync(index, cancellationToken);
                try
                {
                    packages = JsonSerializer.Deserialize<CachedPackage[]>(indexBytes, JsonSettings.Options)
                        ?? throw new InvalidDataException("Invalid cached package inventory.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("Invalid cached package inventory.", exception);
                }
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (packages.Length == 0) throw new InvalidDataException("Cached package inventory is empty.");
                foreach (var package in packages)
                {
                    if (package is null || string.IsNullOrEmpty(package.Id) || !PackageIdValidator.IsValidPackageId(package.Id) || !ids.Add(package.Id)
                        || !NuGetVersion.TryParse(package.OriginalVersion, out _)
                        || !NuGetVersion.TryParse(package.Version, out _)
                        || package.Version != PackageRewriter.VersionFor(package.OriginalVersion,
                            build.PullRequest, build.RunId, build.Attempt, build.Packages.Id))
                        throw new InvalidDataException($"Invalid cached package identity for artifact {build.Packages.Id}.");
                    EnsureClientCacheSafe(package, build);
                    var path = FindCachedPath(directory, package)
                        ?? throw new InvalidDataException($"Cached package {package.Id} is missing from artifact {build.Packages.Id}.");
                    using var reader = new PackageArchiveReader(path);
                    var identity = ReadIdentity(reader, build.Packages.Id);
                    if (!identity.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                        || !identity.Version.ToFullString().Equals(package.Version, StringComparison.Ordinal))
                        throw new InvalidDataException($"Cached package {package.Id} has a mismatched identity in artifact {build.Packages.Id}.");
                    ValidateFeedMetadata(reader, build.Packages.Id);
                    paths.Add(package.Id, path);
                }
                var sealPath = Path.Combine(directory, "integrity.json");
                if (!File.Exists(sealPath))
                    throw new InvalidDataException($"Cached package inventory for artifact {build.Packages.Id} has no integrity record.");
                InventorySeal seal;
                try
                {
                    seal = JsonSerializer.Deserialize<InventorySeal>(
                        await File.ReadAllBytesAsync(sealPath, cancellationToken), JsonSettings.Options)
                        ?? throw new InvalidDataException("Invalid cached package integrity record.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException($"Invalid package integrity record for artifact {build.Packages.Id}.", exception);
                }
                if (seal.SchemaVersion != CacheSchema.Version || seal.ArtifactId != build.Packages.Id
                    || !string.Equals(seal.ArtifactDigest, build.Packages.Digest, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(seal.InventorySha256, Convert.ToHexString(SHA256.HashData(indexBytes)), StringComparison.OrdinalIgnoreCase)
                    || seal.Packages is null || seal.Packages.Length != packages.Length)
                    throw new InvalidDataException($"Cached package inventory integrity failed for artifact {build.Packages.Id}.");
                var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var digest in seal.Packages)
                {
                    if (digest is null || string.IsNullOrEmpty(digest.Id) || string.IsNullOrEmpty(digest.Sha256)
                        || !digests.TryAdd(digest.Id, digest.Sha256))
                        throw new InvalidDataException($"Invalid package integrity record for artifact {build.Packages.Id}.");
                }
                foreach (var package in packages)
                {
                    if (!digests.TryGetValue(package.Id, out var expected)
                        || !string.Equals(expected, await HashFileAsync(paths[package.Id], cancellationToken), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Cached package {package.Id} failed SHA256 integrity verification in artifact {build.Packages.Id}.");
                }
                return packages;
            }
            var archivePath = await artifacts.GetAsync(build.Packages, cancellationToken);
            var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var (originals, packages) = await ReadArtifactAsync(build, archivePath, staging, cancellationToken);
                foreach (var package in packages) EnsureClientCacheSafe(package, build);
                var versions = packages.ToDictionary(x => x.Id, x => x.Version, StringComparer.OrdinalIgnoreCase);
                var digests = new List<PackageDigest>();
                foreach (var package in packages)
                {
                    var original = originals[package.Id];
                    var output = Path.Combine(staging, FileNameFor(package.Id));
                    await PackageRewriter.RewriteAsync(original, output,
                        package.Id, package.OriginalVersion, package.Version, versions, _logger, cancellationToken);
                    using (var reader = new PackageArchiveReader(output))
                    {
                        var identity = ReadIdentity(reader, build.Packages.Id);
                        if (!identity.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                            || !identity.Version.ToFullString().Equals(package.Version, StringComparison.Ordinal))
                            throw new InvalidDataException($"Rewritten package {package.Id} has a mismatched identity in artifact {build.Packages.Id}.");
                        ValidateFeedMetadata(reader, build.Packages.Id);
                    }
                    digests.Add(new PackageDigest(package.Id, await HashFileAsync(output, cancellationToken)));
                    File.Delete(original);
                }
                var stagedIndex = Path.Combine(staging, "packages.json");
                await JsonSettings.WriteAtomicAsync(stagedIndex, packages, cancellationToken);
                await JsonSettings.WriteAtomicAsync(Path.Combine(staging, "integrity.json"),
                    new InventorySeal(CacheSchema.Version, build.Packages.Id, build.Packages.Digest,
                        await HashFileAsync(stagedIndex, cancellationToken), digests.ToArray()), cancellationToken);
                if (Directory.Exists(directory)) throw new InvalidDataException("Incomplete package cache. Remove the affected artifact directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                Directory.Move(staging, directory);
                return packages.ToArray();
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
