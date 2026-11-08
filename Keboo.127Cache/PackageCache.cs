using System.Collections.Concurrent;
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

internal sealed record CachedPackageAvailability(CachedPackage[] Packages, string[] MissingPackageIds, bool HasInventory)
{
    public int MissingPackages => MissingPackageIds.Length;
}

public sealed class PackageCache(ProxyOptions options, ArtifactCache artifacts,
    ILogger<PackageCache>? logger = null, IGitHubClient? github = null)
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
    private sealed record PackageDigest(string Id, string Sha256);
    // The seal detects corruption; migration separately verifies completeness against the raw artifact.
    private sealed record InventorySeal(int SchemaVersion, long ArtifactId, string? ArtifactDigest,
        string InventorySha256, PackageDigest[] Packages);

    private readonly ILogger<PackageCache> _logger = logger ?? NullLogger<PackageCache>.Instance;

    private string DirectoryFor(BuildEntry build) => Path.Combine(options.CacheDirectory, "packages", "discovered", $"v{CacheSchema.Version}",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}");

    private string InventoryPath(BuildEntry build) => Path.Combine(options.CacheDirectory, "inventories",
        $"{build.PullRequest}.{build.RunId}.{build.Attempt}.{build.Packages.Id}.json");

    private SemaphoreSlim GateFor(BuildEntry build) => _gates.GetOrAdd(DirectoryFor(build), _ => new SemaphoreSlim(1));

    internal Task<string> GetVersionArtifactAsync(Artifact artifact, CancellationToken cancellationToken) =>
        github is null ? artifacts.GetAsync(artifact, cancellationToken)
        : _versionArtifactCaches.GetOrAdd(artifact.Id,
            _ => artifacts.CreateIndependent())
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
                    : artifacts.CreateIndependent();
                _packageArtifactCaches.Add(artifact.Id, cache);
            }
        }
        return cache.GetAsync(artifact, cancellationToken);
    }

    internal bool MightHaveCachedContent(RunArtifacts run)
    {
        var build = new BuildEntry(run.RunId, run.Attempt, run.PullRequest, run.HeadSha, "", "", run.Packages, run.VersionArtifact);
        return File.Exists(Path.Combine(DirectoryFor(build), "packages.json")) || File.Exists(InventoryPath(build))
            || artifacts.Contains(run.Packages);
    }

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
        if (OperatingSystem.IsWindows())
        {
            var length = clientFile.Length;
            if (length > 255)
                throw new InvalidDataException($"Package {package.Id} {package.Version} in artifact {build.Packages.Id} requires a {length}-character " +
                    "NuGet client cache filename, exceeding the 255-character Windows component limit. " +
                    "The approved public version cannot be restored safely and was not changed.");
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(clientFile);
        if (bytes > 255)
            throw new InvalidDataException($"Package {package.Id} {package.Version} in artifact {build.Packages.Id} requires a {bytes}-byte " +
                "NuGet client cache filename, exceeding the 255-byte Unix component limit. " +
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
        if (OperatingSystem.IsWindows() ? legacyName.Length > 255 : Encoding.UTF8.GetByteCount(legacyName) > 255) return null;
        path = Path.Combine(directory, legacyName);
        return File.Exists(path) ? path : null;
    }

    public async Task<string> GetAsync(string id, BuildEntry build, CancellationToken cancellationToken)
    {
        CachedPackage[] packages;
        try
        {
            packages = await GetPackagesAsync(build, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            // A package that is intact on disk stays servable when a sibling cannot be restored.
            var available = await GetAvailableOfflineAsync(build, cancellationToken, allowRepair: false);
            var present = available.Packages.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (present is null || FindCachedPath(DirectoryFor(build), present) is not { } path) throw;
            return path;
        }
        var package = packages.SingleOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Package {id} is not present in artifact {build.Packages.Id}.");
        return FindCachedPath(DirectoryFor(build), package)
            ?? throw new InvalidDataException($"Cached package {id} is missing from artifact {build.Packages.Id}.");
    }

    internal async Task VerifyForMigrationAsync(BuildEntry build, CancellationToken cancellationToken)
    {
        var packages = await GetPackagesAsync(build, cancellationToken);
        var archivePath = await GetPackageArtifactAsync(build.Packages, cancellationToken);
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
        var gate = GateFor(build);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(index))
            {
                var inventory = ReadInventoryOrNull(index);
                if (ValidInventory(inventory, build) && inventory!.Any(package => FindCachedPath(directory, package) is null))
                    await RestoreFromArtifactAsync(build, directory, inventory, cancellationToken);
                var packages = await VerifyCachedAsync(build, directory, index, cancellationToken);
                var retained = await ReadRetainedInventoryAsync(build, cancellationToken);
                if (retained is not null && !SameInventory(retained, packages))
                    throw new InvalidDataException("Cached package inventory does not agree with the retained inventory.");
                await SaveRetainedInventoryAsync(build, packages, cancellationToken);
                return packages;
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

    private static CachedPackage[]? ReadInventoryOrNull(string index)
    {
        try
        {
            return JsonSerializer.Deserialize<CachedPackage[]>(File.ReadAllBytes(index), JsonSettings.Options);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
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
        FindCachedPath(directory, package) ?? Path.Combine(directory, FileNameFor(package.Id));

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
            || data.VersionId != (build.VersionArtifact?.Id ?? 0) || data.VersionName != (build.VersionArtifact?.Name ?? "")
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
                throw new InvalidDataException("Cached package inventory does not agree with the retained inventory.");
            return;
        }
        var data = new InventoryData(options.Repository, options.Workflow, build.RunId, build.Attempt, build.PullRequest,
            build.HeadSha, build.Packages.Id, build.Packages.Name, build.VersionArtifact?.Id ?? 0, build.VersionArtifact?.Name ?? "", packages);
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
            var seal = Path.Combine(directory, "integrity.json");
            if (!File.Exists(seal) && File.Exists(Path.Combine(staging, "integrity.json")))
                File.Move(Path.Combine(staging, "integrity.json"), seal);
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

    private async Task<CachedPackage[]> VerifyCachedAsync(
        BuildEntry build, string directory, string index, CancellationToken cancellationToken)
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

    private async Task<CachedPackage[]> CreateStagingAsync(BuildEntry build, string staging, CancellationToken cancellationToken)
    {
        var archivePath = await GetPackageArtifactAsync(build.Packages, cancellationToken);
        Directory.CreateDirectory(staging);
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
        return packages.ToArray();
        }
    }
}