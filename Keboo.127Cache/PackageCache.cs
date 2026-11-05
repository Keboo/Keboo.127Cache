using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Keboo._127Cache;

public sealed record CachedPackage(string Id, string OriginalVersion, string Version);

public sealed class PackageCache(ProxyOptions options, ArtifactCache artifacts, ILogger<PackageCache>? logger = null)
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ILogger<PackageCache> _logger = logger ?? NullLogger<PackageCache>.Instance;

    private string DirectoryFor(BuildEntry build) => Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}");

    private static string FileNameFor(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()))) + ".nupkg";

    private static PackageIdentity ReadIdentity(PackageArchiveReader reader, long artifactId)
    {
        try { return reader.GetIdentity(); }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Invalid package identity in artifact {artifactId}.", exception);
        }
    }

    private static string? FindCachedPath(string directory, CachedPackage package)
    {
        var path = Path.Combine(directory, FileNameFor(package.Id));
        if (File.Exists(path)) return path;
        // Earlier drafts of schema v2 used the public identity as the filename.
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
        foreach (var package in await GetPackagesAsync(build, cancellationToken))
        {
            var path = FindCachedPath(DirectoryFor(build), package)
                ?? throw new InvalidDataException($"Cached package {package.Id} is missing from artifact {build.Packages.Id}.");
            using var reader = new PackageArchiveReader(path);
            var identity = ReadIdentity(reader, build.Packages.Id);
            if (!identity.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                || !identity.Version.ToFullString().Equals(package.Version, StringComparison.Ordinal))
                throw new InvalidDataException($"Cached package {package.Id} has a mismatched identity in artifact {build.Packages.Id}.");
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
                try
                {
                    packages = JsonSerializer.Deserialize<CachedPackage[]>(
                        await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options)
                        ?? throw new InvalidDataException("Invalid cached package inventory.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("Invalid cached package inventory.", exception);
                }
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (packages.Length == 0) throw new InvalidDataException("Cached package inventory is empty.");
                foreach (var package in packages)
                {
                    if (package is null || string.IsNullOrEmpty(package.Id) || !PackageIdValidator.IsValidPackageId(package.Id) || !ids.Add(package.Id)
                        || !NuGetVersion.TryParse(package.OriginalVersion, out _)
                        || !NuGetVersion.TryParse(package.Version, out _)
                        || package.Version != PackageRewriter.VersionFor(package.OriginalVersion,
                            build.PullRequest, build.RunId, build.Attempt, build.Packages.Id))
                        throw new InvalidDataException($"Invalid cached package identity for artifact {build.Packages.Id}.");
                    if (FindCachedPath(directory, package) is null)
                        throw new InvalidDataException($"Cached package {package.Id} is missing from artifact {build.Packages.Id}.");
                }
                return packages;
            }
            var archivePath = await artifacts.GetAsync(build.Packages, cancellationToken);
            var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var packages = new List<CachedPackage>();
                using (var archive = ZipFile.OpenRead(archivePath))
                {
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
                }
                if (packages.Count == 0)
                    throw new InvalidDataException($"Artifact {build.Packages.Id} contains no NuGet packages.");
                var versions = packages.ToDictionary(x => x.Id, x => x.Version, StringComparer.OrdinalIgnoreCase);
                foreach (var package in packages)
                {
                    var original = originals[package.Id];
                    await PackageRewriter.RewriteAsync(original, Path.Combine(staging, FileNameFor(package.Id)),
                        package.Id, package.OriginalVersion, package.Version, versions, _logger, cancellationToken);
                    File.Delete(original);
                }
                await JsonSettings.WriteAtomicAsync(Path.Combine(staging, "packages.json"), packages, cancellationToken);
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
