using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging;

namespace Keboo._127Cache;

public sealed record CachedPackage(string Id, string OriginalVersion, string Version);

public sealed class PackageCache(ProxyOptions options, ArtifactCache artifacts, ILogger<PackageCache>? logger = null)
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ILogger<PackageCache> _logger = logger ?? NullLogger<PackageCache>.Instance;

    private string DirectoryFor(BuildEntry build) => Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}");

    public async Task<string> GetAsync(string id, BuildEntry build, CancellationToken cancellationToken)
    {
        var packages = await GetPackagesAsync(build, cancellationToken);
        var package = packages.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Package {id} is not present in artifact {build.Packages.Id}.");
        return Path.Combine(DirectoryFor(build), $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");
    }

    public async Task<CachedPackage[]> GetPackagesAsync(BuildEntry build, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(build);
        var index = Path.Combine(directory, "packages.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(index))
                return JsonSerializer.Deserialize<CachedPackage[]>(await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options)
                    ?? throw new InvalidDataException("Invalid cached package inventory.");
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
                        var identity = reader.GetIdentity();
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
                    await PackageRewriter.RewriteAsync(original, Path.Combine(staging, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg"),
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
