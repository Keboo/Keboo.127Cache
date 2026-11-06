using System.IO.Compression;
using System.Text.Json;

using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache;

public sealed record CachedPackage(string Id, string OriginalVersion, string Version);
internal sealed record CachedPackageAvailability(CachedPackage[] Packages, int MissingPackages, bool HasInventory);

public sealed class PackageCache(ProxyOptions options, ArtifactCache artifacts)
{
    private readonly SemaphoreSlim _gate = new(1);

    private string DirectoryFor(BuildEntry build) => Path.Combine(options.CacheDirectory, "packages", "discovered",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}");

    public async Task<string> GetAsync(string id, BuildEntry build, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(build);
        var index = Path.Combine(directory, "packages.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(index))
            {
                var existing = JsonSerializer.Deserialize<CachedPackage[]>(
                    await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options);
                if (!ValidInventory(existing, build)) throw new InvalidDataException("Invalid cached package inventory.");
                var present = existing!.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (present is not null && File.Exists(PackagePath(directory, present)))
                {
                    var existingPath = PackagePath(directory, present);
                    if (!IsReadablePackage(existingPath, present))
                        throw new InvalidDataException($"Cached package {id} is invalid in artifact {build.Packages.Id}.");
                    return existingPath;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        var packages = await GetPackagesAsync(build, cancellationToken);
        var package = packages.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Package {id} is not present in artifact {build.Packages.Id}.");
        var path = PackagePath(directory, package);
        if (!File.Exists(path)) throw new InvalidDataException($"Cached package {id} is missing from artifact {build.Packages.Id}.");
        if (!IsReadablePackage(path, package))
            throw new InvalidDataException($"Cached package {id} is invalid in artifact {build.Packages.Id}.");
        return path;
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
                var cached = JsonSerializer.Deserialize<CachedPackage[]>(
                    await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options);
                if (cached is null || !ValidInventory(cached, build))
                    throw new InvalidDataException("Invalid cached package inventory.");
                if (cached.Any(package => !File.Exists(PackagePath(directory, package))))
                {
                    await RestoreFromArtifactAsync(build, directory, cached, cancellationToken);
                    if (cached.Any(package => !File.Exists(PackagePath(directory, package))))
                        throw new InvalidDataException($"Cached packages for artifact {build.Packages.Id} are incomplete.");
                }
                return cached;
            }
            var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                var packages = await CreateStagingAsync(build, staging, cancellationToken);
                if (Directory.Exists(directory)) throw new InvalidDataException("Incomplete package cache. Remove the affected artifact directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                Directory.Move(staging, directory);
                return packages;
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

    internal async Task<CachedPackageAvailability> GetAvailableOfflineAsync(BuildEntry build, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(build);
        var index = Path.Combine(directory, "packages.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CachedPackage[]? inventory;
            var attemptedRepair = false;
            if (File.Exists(index))
            {
                try
                {
                    inventory = JsonSerializer.Deserialize<CachedPackage[]>(
                        await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                {
                    return new([], 0, false);
                }
                if (!ValidInventory(inventory, build)) return new([], 0, false);
            }
            else
            {
                if (!artifacts.Contains(build.Packages)) return new([], 0, false);
                attemptedRepair = true;
                try
                {
                    inventory = await RestoreFromArtifactAsync(build, directory, null, cancellationToken);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                {
                    return new([], 0, false);
                }
                if (!ValidInventory(inventory, build)) return new([], 0, false);
            }

            var available = AvailablePackages(directory, inventory!);
            if (available.Length != inventory!.Length && !attemptedRepair && artifacts.Contains(build.Packages))
            {
                try
                {
                    await RestoreFromArtifactAsync(build, directory, inventory, cancellationToken);
                    available = AvailablePackages(directory, inventory);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                {
                    // Preserve the existing files and use only packages that are still readable.
                }
            }
            return new(available, inventory.Length - available.Length, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string PackagePath(string directory, CachedPackage package) =>
        Path.Combine(directory, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");

    private static bool ValidInventory(CachedPackage[]? packages, BuildEntry build)
    {
        if (packages is not { Length: > 0 }
            || packages.Any(package => package is null || !PackageIdValidator.IsValidPackageId(package.Id)
                || !NuGetVersion.TryParse(package.OriginalVersion, out _) || !NuGetVersion.TryParse(package.Version, out _))
            || packages.Select(package => package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Length)
            return false;

        return packages.All(package => package.Version == PackageRewriter.VersionFor(
            package.OriginalVersion, build.PullRequest, build.RunId, build.Attempt, build.Packages.Id));
    }

    private static CachedPackage[] AvailablePackages(string directory, CachedPackage[] inventory)
    {
        var available = new List<CachedPackage>();
        foreach (var package in inventory)
        {
            if (IsReadablePackage(PackagePath(directory, package), package))
                available.Add(package);
        }
        return available.ToArray();
    }

    private static bool IsReadablePackage(string path, CachedPackage package)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var reader = new PackageArchiveReader(path);
            var identity = reader.GetIdentity();
            return identity.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                && identity.Version.ToNormalizedString() == package.Version;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<CachedPackage[]?> RestoreFromArtifactAsync(
        BuildEntry build, string directory, CachedPackage[]? existing, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var generated = await CreateStagingAsync(build, staging, cancellationToken);
            if (existing is not null && !existing.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(generated.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)))
                return null;

            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            if (!Directory.Exists(directory))
            {
                Directory.Move(staging, directory);
                return generated;
            }

            foreach (var package in generated)
            {
                var destination = PackagePath(directory, package);
                if (File.Exists(destination)) continue;
                try
                {
                    File.Move(PackagePath(staging, package), destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                }
            }
            var index = Path.Combine(directory, "packages.json");
            if (existing is null && !File.Exists(index))
                await JsonSettings.WriteAtomicAsync(index, generated, cancellationToken);
            return existing ?? generated;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<CachedPackage[]> CreateStagingAsync(BuildEntry build, string staging, CancellationToken cancellationToken)
    {
        var archivePath = await artifacts.GetAsync(build.Packages, cancellationToken);
        Directory.CreateDirectory(staging);
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<CachedPackage>();
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)))
            {
                // Artifact paths never become local filenames.
                var original = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".original");
                entry.ExtractToFile(original);
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
            PackageRewriter.Rewrite(original, PackagePath(staging, package),
                package.Id, package.OriginalVersion, package.Version, versions);
            File.Delete(original);
        }
        await JsonSettings.WriteAtomicAsync(Path.Combine(staging, "packages.json"), packages, cancellationToken);
        return packages.ToArray();
    }
}