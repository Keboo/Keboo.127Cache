using Microsoft.Extensions.Logging.Abstractions;

namespace Keboo._127Cache;

public sealed class ArtifactCache
{
    private static readonly SemaphoreSlim[] PublicationGates =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
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

                var publication = PublicationGate(path);
                await publication.WaitAsync(cancellationToken);
                try
                {
                    await using var held = await AcquirePublicationLockAsync(path, cancellationToken);
                    await _integrity.WriteRecordAsync(path, artifact.Id, downloaded.Digest, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _integrity.EnsureUnchanged(temporary, artifact.Id, downloaded);
                    try
                    {
                        File.Move(temporary, path);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        var expected = supplied ?? await _integrity.ReadRequiredRecordAsync(path, artifact.Id, cancellationToken);
                        await _integrity.VerifyAsync(path, artifact.Id, expected, cancellationToken);
                        return path;
                    }

                    _integrity.RememberPublished(path, artifact.Id, downloaded);
                    return path;
                }
                finally
                {
                    publication.Release();
                }
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

    private static SemaphoreSlim PublicationGate(string path)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hash = (uint)comparer.GetHashCode(Path.GetFullPath(path));
        return PublicationGates[(int)(hash % PublicationGates.Length)];
    }

    private static async Task<FileStream> AcquirePublicationLockAsync(string path, CancellationToken cancellationToken)
    {
        // Unix File.Move without overwrite can race between its existence check and rename.
        // Keep the lock file stable so concurrent processes serialize the record and ZIP publication.
        for (;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var code = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows() ? code is 32 or 33
            : OperatingSystem.IsLinux() ? code == 11
            : (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) && code == 35;
    }
}
