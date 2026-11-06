using System.Security.Cryptography;
using System.Text;

namespace Keboo._127Cache;

internal sealed class ArtifactIntegrityStore(ILogger<ArtifactCache> logger)
{
    private const int DigestLength = 64;
    private static readonly EventId HashComputed = new(1301, "ArtifactSha256Computed");
    private readonly Dictionary<string, VerifiedFile> _verified = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal readonly record struct FileStamp(long Length, long LastWriteTicks, long CreationTicks);
    internal readonly record struct HashedFile(string Digest, FileStamp Stamp);
    private readonly record struct VerifiedFile(string Digest, FileStamp Stamp);

    private static string RecordPath(string path) => path + ".sha256";

    public static string? GetSuppliedDigest(Artifact artifact)
    {
        if (artifact.Digest is null) return null;
        const string prefix = "sha256:";
        if (!artifact.Digest.StartsWith(prefix, StringComparison.Ordinal)
            || !IsDigest(artifact.Digest.AsSpan(prefix.Length)))
            throw new InvalidDataException($"Artifact {artifact.Id} has an unsupported or invalid SHA256 digest.");
        return artifact.Digest[prefix.Length..].ToUpperInvariant();
    }

    private static bool IsDigest(ReadOnlySpan<char> digest)
    {
        if (digest.Length != DigestLength) return false;
        foreach (var character in digest)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    public static void RequireMatch(string actual, string expected, long artifactId)
    {
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact {artifactId} failed SHA256 verification.");
    }

    public async Task<string?> TryReadRecordAsync(string path, long artifactId, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(RecordPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: DigestLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            if (stream.Length != DigestLength)
                throw InvalidRecord(artifactId);
            var bytes = new byte[DigestLength];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var digest = Encoding.ASCII.GetString(bytes);
            if (stream.Length != DigestLength || !IsDigest(digest))
                throw InvalidRecord(artifactId);
            return digest.ToUpperInvariant();
        }
    }

    public async Task<string> ReadRequiredRecordAsync(string path, long artifactId, CancellationToken cancellationToken) =>
        await TryReadRecordAsync(path, artifactId, cancellationToken)
        ?? throw new InvalidDataException(
            $"Artifact {artifactId} is missing its local SHA256 record. Restore the record or explicitly evict the artifact.");

    private static InvalidDataException InvalidRecord(long artifactId) =>
        new($"Artifact {artifactId} has an invalid local SHA256 record. Restore the record or explicitly evict the artifact.");

    public async Task WriteRecordAsync(string path, long artifactId, string digest, CancellationToken cancellationToken)
    {
        var record = RecordPath(path);
        if (await TryReadRecordAsync(path, artifactId, cancellationToken) is null)
        {
            var temporary = record + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: DigestLength, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(digest), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, record);
                }
                catch (IOException) when (File.Exists(record))
                {
                    // Another writer published its record first; compare it below.
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        RequireMatch(digest, await ReadRequiredRecordAsync(path, artifactId, cancellationToken), artifactId);
    }

    public async Task VerifyAsync(string path, long artifactId, string expected, CancellationToken cancellationToken)
    {
        var stamp = GetStamp(path);
        if (_verified.TryGetValue(path, out var verified)
            && verified.Stamp == stamp
            && verified.Digest.Equals(expected, StringComparison.Ordinal))
            return;

        var actual = await HashAsync(path, artifactId, cancellationToken);
        RequireMatch(actual.Digest, expected, artifactId);
        _verified[path] = new VerifiedFile(actual.Digest, actual.Stamp);
    }

    public async Task<HashedFile> HashAsync(string path, long artifactId, CancellationToken cancellationToken)
    {
        var before = GetStamp(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var after = GetStamp(path);
        if (before != after || stream.Length != after.Length)
            throw new InvalidDataException($"Artifact {artifactId} changed while its SHA256 was being computed.");
        logger.LogDebug(HashComputed, "Computed SHA256 for artifact {ArtifactId}.", artifactId);
        return new HashedFile(digest, after);
    }

    public void EnsureUnchanged(string path, long artifactId, HashedFile verified)
    {
        if (GetStamp(path) != verified.Stamp)
            throw new InvalidDataException($"Artifact {artifactId} changed before it could be cached.");
    }

    public void RememberPublished(string path, long artifactId, HashedFile verified)
    {
        var stamp = GetStamp(path);
        if (stamp.Length != verified.Stamp.Length || stamp.LastWriteTicks != verified.Stamp.LastWriteTicks)
            throw new InvalidDataException($"Artifact {artifactId} changed while it was being cached.");
        _verified[path] = new VerifiedFile(verified.Digest, stamp);
    }

    public void Forget(string path) => _verified.Remove(path);

    private static FileStamp GetStamp(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
    }
}
