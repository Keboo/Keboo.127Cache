using Microsoft.Extensions.Logging.Abstractions;

namespace Keboo._127Cache;

public sealed class ArtifactCache
{
    private static readonly Dictionary<string, PublicationGate> PublicationGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ProxyOptions _options;
    private readonly IGitHubClient _github;
    private readonly ILogger<ArtifactCache> _logger;
    private readonly ArtifactIntegrityStore _integrity;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HashSet<long> _warned = [];

    public ArtifactCache(ProxyOptions options, IGitHubClient github)
        : this(options, github, NullLogger<ArtifactCache>.Instance)
    {
    }

    public ArtifactCache(ProxyOptions options, IGitHubClient github, ILogger<ArtifactCache> logger)
    {
        _options = options;
        _github = github;
        _logger = logger;
        _integrity = new ArtifactIntegrityStore(logger);
    }

    public bool Contains(Artifact artifact) => File.Exists(GetPath(artifact));

    private string GetPath(Artifact artifact) => Path.Combine(_options.CacheDirectory, "artifacts", $"{artifact.Id}.zip");

    public async Task<string> GetAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var path = GetPath(artifact);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (artifact.Digest is null && _warned.Add(artifact.Id))
                _logger.LogWarning(
                    "Artifact {ArtifactId} has no upstream SHA256 digest; subsequent integrity checks use its locally recorded first download.",
                    artifact.Id);

            var supplied = ArtifactIntegrityStore.GetSuppliedDigest(artifact);
            if (File.Exists(path))
            {
                var expected = supplied ?? await _integrity.ReadRequiredRecordAsync(path, artifact.Id, cancellationToken);
                await _integrity.VerifyAsync(path, artifact.Id, expected, cancellationToken);
                return path;
            }

            if (artifact.Expired) throw new IOException($"Artifact {artifact.Id} has expired and is not cached locally.");

            var recorded = await _integrity.TryReadRecordAsync(path, artifact.Id, cancellationToken);
            if (recorded is not null && supplied is not null)
                ArtifactIntegrityStore.RequireMatch(recorded, supplied, artifact.Id);

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"{artifact.Id}.{Guid.NewGuid():N}.tmp");
            try
            {
                await _github.DownloadAsync(artifact, temporary, cancellationToken);
                var downloaded = await _integrity.HashAsync(temporary, artifact.Id, cancellationToken);
                if (supplied is not null) ArtifactIntegrityStore.RequireMatch(downloaded.Digest, supplied, artifact.Id);
                if (recorded is not null) ArtifactIntegrityStore.RequireMatch(downloaded.Digest, recorded, artifact.Id);
                return await PublishAsync(path, artifact.Id, supplied, temporary, downloaded, cancellationToken);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch
        {
            _integrity.Forget(path);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> PublishAsync(
        string path, long artifactId, string? supplied, string temporary,
        ArtifactIntegrityStore.HashedFile downloaded, CancellationToken cancellationToken)
    {
        var publicationPath = Path.GetFullPath(path);
        var publication = ReferencePublicationGate(publicationPath);
        try
        {
            await publication.Semaphore.WaitAsync(cancellationToken);
            try
            {
                await using var held = await AcquirePublicationLockAsync(path, cancellationToken);
                _integrity.EnsureUnchanged(temporary, artifactId, downloaded);
                if (File.Exists(path))
                    return await VerifyPublishedAsync(path, artifactId, supplied, downloaded.Digest, cancellationToken);

                await _integrity.WriteRecordAsync(path, artifactId, downloaded.Digest, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _integrity.EnsureUnchanged(temporary, artifactId, downloaded);
                try
                {
                    File.Move(temporary, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    return await VerifyPublishedAsync(path, artifactId, supplied, downloaded.Digest, cancellationToken);
                }

                _integrity.RememberPublished(path, artifactId, downloaded);
                return path;
            }
            finally
            {
                publication.Semaphore.Release();
            }
        }
        finally
        {
            DereferencePublicationGate(publicationPath, publication);
        }
    }

    private async Task<string> VerifyPublishedAsync(
        string path, long artifactId, string? supplied, string downloaded, CancellationToken cancellationToken)
    {
        var expected = supplied ?? await _integrity.ReadRequiredRecordAsync(path, artifactId, cancellationToken);
        ArtifactIntegrityStore.RequireMatch(downloaded, expected, artifactId);
        await _integrity.VerifyAsync(path, artifactId, expected, cancellationToken);
        return path;
    }

    private sealed class PublicationGate
    {
        public readonly SemaphoreSlim Semaphore = new(1);
        public int References;
    }

    private static PublicationGate ReferencePublicationGate(string path)
    {
        lock (PublicationGates)
        {
            if (!PublicationGates.TryGetValue(path, out var publication))
                PublicationGates.Add(path, publication = new PublicationGate());
            publication.References++;
            return publication;
        }
    }

    private static void DereferencePublicationGate(string path, PublicationGate publication)
    {
        lock (PublicationGates)
        {
            // Waiters retain references so a replacement gate cannot overlap an existing publisher.
            if (--publication.References == 0)
            {
                PublicationGates.Remove(path);
                publication.Semaphore.Dispose();
            }
        }
    }

    private static async Task<FileStream> AcquirePublicationLockAsync(string path, CancellationToken cancellationToken)
    {
        // The lock file must stay in place: deleting it can split cooperating writers across different files.
        var lockPath = path + ".lock";
        for (;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream held;
            try
            {
                held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                continue;
            }

            try
            {
                EnsureExclusiveLock(lockPath);
                return held;
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }
    }

    private static void EnsureExclusiveLock(string lockPath)
    {
        // On Unix FileStream can silently ignore unsupported or disabled advisory locks.
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
            return;
        }

        throw new IOException($"Exclusive file locking is unavailable for '{lockPath}'; artifact publication is disabled.");
    }

    private static bool IsLockContention(IOException exception)
    {
        var code = exception.HResult & 0xffff;
        return code is 32 or 33
            || (OperatingSystem.IsLinux() && code == 11)
            || ((OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) && code is 11 or 35);
    }
}
