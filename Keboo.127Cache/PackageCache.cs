using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache;

public sealed record CachedPackage(string Id, string OriginalVersion, string Version);
internal sealed record CachedPackageAvailability(CachedPackage[] Packages, string[] MissingPackageIds, bool HasInventory)
{
    public int MissingPackages => MissingPackageIds.Length;
}

public sealed class PackageCache(ProxyOptions options, ArtifactCache artifacts,
    IGitHubClient? github = null, ILogger<ArtifactCache>? logger = null)
{
    private sealed record InventoryData(
        string Repository, string Workflow, long RunId, int Attempt, int PullRequest, string HeadSha,
        long PackagesId, string PackagesName, long VersionId, string VersionName, CachedPackage[] Packages);
    private sealed record InventoryRecord(int SchemaVersion, InventoryData Inventory, string Sha256);

    private const int InventorySchemaVersion = 1;
    private static readonly uint[] CrcTable = CreateCrcTable();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, ArtifactCache> _versionArtifactCaches = new();
    private readonly Dictionary<long, ArtifactCache> _packageArtifactCaches = [];

    private string DirectoryFor(BuildEntry build) => Path.Combine(options.CacheDirectory, "packages", "discovered",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}");

    private string InventoryPath(BuildEntry build) => Path.Combine(options.CacheDirectory, "inventories",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}.json");

    private SemaphoreSlim GateFor(BuildEntry build) => _gates.GetOrAdd(DirectoryFor(build), _ => new SemaphoreSlim(1));

    internal Task<string> GetVersionArtifactAsync(Artifact artifact, CancellationToken cancellationToken) =>
        github is null ? artifacts.GetAsync(artifact, cancellationToken)
        : _versionArtifactCaches.GetOrAdd(artifact.Id,
            _ => new ArtifactCache(options, github, logger ?? NullLogger<ArtifactCache>.Instance))
            .GetAsync(artifact, cancellationToken);

    private Task<string> GetPackageArtifactAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        if (github is null) return artifacts.GetAsync(artifact, cancellationToken);
        ArtifactCache cache;
        lock (_packageArtifactCaches)
        {
            if (!_packageArtifactCaches.TryGetValue(artifact.Id, out cache!))
            {
                cache = _packageArtifactCaches.Count == 0 ? artifacts
                    : new ArtifactCache(options, github, logger ?? NullLogger<ArtifactCache>.Instance);
                _packageArtifactCaches.Add(artifact.Id, cache);
            }
        }
        return cache.GetAsync(artifact, cancellationToken);
    }

    internal bool MightHaveCachedContent(RunArtifacts run)
    {
        var directory = Path.Combine(options.CacheDirectory, "packages", "discovered",
            $"{run.PullRequest}.{run.RunId}.{run.Attempt}.{run.Packages.Id}");
        var retained = Path.Combine(options.CacheDirectory, "inventories",
            $"{run.PullRequest}.{run.RunId}.{run.Attempt}.{run.Packages.Id}.json");
        return File.Exists(Path.Combine(directory, "packages.json")) || File.Exists(retained) || artifacts.Contains(run.Packages);
    }

    public async Task<string> GetAsync(string id, BuildEntry build, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(build);
        var index = Path.Combine(directory, "packages.json");
        var retained = await ReadRetainedInventoryAsync(build, cancellationToken);
        CachedPackage[]? existing = retained;
        if (File.Exists(index))
        {
            existing = JsonSerializer.Deserialize<CachedPackage[]>(
                await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options);
            if (!ValidInventory(existing, build)) throw new InvalidDataException("Invalid cached package inventory.");
            if (retained is not null && !SameInventory(retained, existing!))
                throw new InvalidDataException("Cached package inventories do not agree.");
        }
        var present = existing?.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (present is not null && File.Exists(PackagePath(directory, present)))
        {
            var existingPath = PackagePath(directory, present);
            if (!IsReadablePackage(existingPath, present))
                throw new InvalidDataException($"Cached package {id} is invalid in artifact {build.Packages.Id}.");
            return existingPath;
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
        var gate = GateFor(build);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(index))
            {
                var cached = JsonSerializer.Deserialize<CachedPackage[]>(
                    await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options);
                if (cached is null || !ValidInventory(cached, build))
                    throw new InvalidDataException("Invalid cached package inventory.");
                var retained = await ReadRetainedInventoryAsync(build, cancellationToken);
                if (retained is not null && !SameInventory(retained, cached))
                    throw new InvalidDataException("Cached package inventories do not agree.");
                if (cached.Any(package => !File.Exists(PackagePath(directory, package))))
                {
                    await RestoreFromArtifactAsync(build, directory, cached, cancellationToken);
                    if (cached.Any(package => !File.Exists(PackagePath(directory, package))))
                        throw new InvalidDataException($"Cached packages for artifact {build.Packages.Id} are incomplete.");
                }
                if (cached.Any(package => !IsReadablePackage(PackagePath(directory, package), package)))
                    throw new InvalidDataException($"Cached packages for artifact {build.Packages.Id} are invalid.");
                await SaveRetainedInventoryAsync(build, cached, cancellationToken);
                return cached;
            }
            var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                var packages = await CreateStagingAsync(build, staging, cancellationToken);
                if (Directory.Exists(directory)) throw new InvalidDataException("Incomplete package cache. Remove the affected artifact directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                Directory.Move(staging, directory);
                await SaveRetainedInventoryAsync(build, packages, cancellationToken);
                return packages;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<CachedPackageAvailability> GetAvailableOfflineAsync(
        BuildEntry build, CancellationToken cancellationToken, bool allowRepair = true)
    {
        var directory = DirectoryFor(build);
        var index = Path.Combine(directory, "packages.json");
        var gate = GateFor(build);
        var ownsGate = await gate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        try
        {
            CachedPackage[]? inventory;
            var attemptedRepair = false;
            CachedPackage[]? retained;
            try
            {
                retained = await ReadRetainedInventoryAsync(build, cancellationToken);
            }
            catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
            {
                return new([], [], false);
            }
            if (File.Exists(index))
            {
                try
                {
                    inventory = JsonSerializer.Deserialize<CachedPackage[]>(
                        await File.ReadAllTextAsync(index, cancellationToken), JsonSettings.Options);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                {
                    return new([], [], false);
                }
                if (!ValidInventory(inventory, build)) return new([], [], false);
                if (retained is not null && !SameInventory(retained, inventory!)) return new([], [], false);
            }
            else if (retained is not null)
                inventory = retained;
            else
            {
                if (!allowRepair || !ownsGate || !artifacts.Contains(build.Packages)) return new([], [], false);
                attemptedRepair = true;
                try
                {
                    inventory = await RestoreFromArtifactAsync(build, directory, null, cancellationToken);
                }
                catch (Exception exception) when (DiscoveryFailure.CanHandle(exception, cancellationToken))
                {
                    return new([], [], false);
                }
                if (!ValidInventory(inventory, build)) return new([], [], false);
            }

            var available = AvailablePackages(directory, inventory!);
            if (allowRepair && ownsGate && available.Length != inventory!.Length && !attemptedRepair && artifacts.Contains(build.Packages))
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
            var availableIds = available.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new(available, inventory!.Where(package => !availableIds.Contains(package.Id))
                .Select(package => package.Id).ToArray(), true);
        }
        finally
        {
            if (ownsGate) gate.Release();
        }
    }

    private static string PackagePath(string directory, CachedPackage package) =>
        Path.Combine(directory, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");

    private async Task<CachedPackage[]?> ReadRetainedInventoryAsync(BuildEntry build, CancellationToken cancellationToken)
    {
        var path = InventoryPath(build);
        if (!File.Exists(path)) return null;
        var record = JsonSerializer.Deserialize<InventoryRecord>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonSettings.Options);
        var data = record?.Inventory;
        if (record is null || record.SchemaVersion != InventorySchemaVersion || data is null
            || data.Repository != options.Repository || data.Workflow != options.Workflow
            || data.RunId != build.RunId || data.Attempt != build.Attempt || data.PullRequest != build.PullRequest
            || data.HeadSha != build.HeadSha || data.PackagesId != build.Packages.Id || data.PackagesName != build.Packages.Name
            || data.VersionId != build.VersionArtifact.Id || data.VersionName != build.VersionArtifact.Name
            || !ValidInventory(data.Packages, build) || record.Sha256 != InventoryDigest(data))
            throw new InvalidDataException("The retained package inventory is invalid or belongs to another build.");
        return data.Packages;
    }

    private async Task SaveRetainedInventoryAsync(BuildEntry build, CachedPackage[] packages, CancellationToken cancellationToken)
    {
        var retained = await ReadRetainedInventoryAsync(build, cancellationToken);
        if (retained is not null)
        {
            if (!SameInventory(retained, packages))
                throw new InvalidDataException("Cached package inventories do not agree.");
            return;
        }
        var data = new InventoryData(options.Repository, options.Workflow, build.RunId, build.Attempt, build.PullRequest,
            build.HeadSha, build.Packages.Id, build.Packages.Name, build.VersionArtifact.Id, build.VersionArtifact.Name, packages);
        var path = InventoryPath(build);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await JsonSettings.WriteAtomicAsync(path, new InventoryRecord(InventorySchemaVersion, data, InventoryDigest(data)), cancellationToken);
    }

    private static string InventoryDigest(InventoryData data) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data, JsonSettings.Options)));

    private static bool SameInventory(CachedPackage[] first, CachedPackage[] second) =>
        first.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(second.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase));

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
                && identity.Version.ToNormalizedString() == package.Version
                && HasValidZipPayload(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool HasValidZipPayload(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var buffer = new byte[64 * 1024];
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            var crc = uint.MaxValue;
            long length = 0;
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                length += count;
                for (var index = 0; index < count; index++)
                    crc = (crc >> 8) ^ CrcTable[(int)((crc ^ buffer[index]) & 0xff)];
            }
            if (length != entry.Length || ~crc != entry.Crc32) return false;
        }
        return true;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 1 ? 0xedb88320u : 0u);
            table[index] = value;
        }
        return table;
    }

    private async Task<CachedPackage[]?> RestoreFromArtifactAsync(
        BuildEntry build, string directory, CachedPackage[]? existing, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(options.CacheDirectory, "staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var generated = await CreateStagingAsync(build, staging, cancellationToken);
            if (existing is not null && !SameInventory(existing, generated))
                return null;

            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            if (!Directory.Exists(directory))
            {
                Directory.Move(staging, directory);
                await SaveRetainedInventoryAsync(build, generated, cancellationToken);
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
            if (!File.Exists(index))
                await JsonSettings.WriteAtomicAsync(index, generated, cancellationToken);
            await SaveRetainedInventoryAsync(build, generated, cancellationToken);
            return existing ?? generated;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<CachedPackage[]> CreateStagingAsync(BuildEntry build, string staging, CancellationToken cancellationToken)
    {
        var archivePath = await GetPackageArtifactAsync(build.Packages, cancellationToken);
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
                if (!HasValidZipPayload(original))
                    throw new InvalidDataException($"Package payload in artifact {build.Packages.Id} has a CRC mismatch.");
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
            if (!IsReadablePackage(PackagePath(staging, package), package))
                throw new InvalidDataException($"Rewritten package {package.Id} has an invalid payload.");
        }
        await JsonSettings.WriteAtomicAsync(Path.Combine(staging, "packages.json"), packages, cancellationToken);
        return packages.ToArray();
    }
}