using System.Text.RegularExpressions;

namespace Keboo.LocalCache;

internal readonly record struct BuildKey(int PullRequest, long RunId, int Attempt);

internal sealed class GateRelease(SemaphoreSlim gate) : IDisposable
{
    private int _released;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
    }
}

internal sealed class HeldLocks(List<IDisposable> items) : IDisposable
{
    public void Dispose()
    {
        for (var index = items.Count - 1; index >= 0; index--) items[index].Dispose();
        items.Clear();
    }
}

// Reads the on-disk layout of one source directory without trusting any metadata file.
internal static partial class CacheDisk
{
    public const string TrashPrefix = ".evicting-";

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)\.(\d+)(\.json)?$")]
    private static partial Regex BuildName();

    // Package directories (every schema version) and retained inventories are named {pr}.{run}.{attempt}.{packagesArtifactId}.
    public static IEnumerable<(BuildKey Key, long ArtifactId, string Path)> ScanBuildEntries(string directory)
    {
        var discovered = Path.Combine(directory, "packages", "discovered");
        if (Directory.Exists(discovered))
        {
            foreach (var entry in SafeEnumerate(discovered, directories: true))
            {
                if (TryParse(Path.GetFileName(entry), out var key, out var artifact))
                    yield return (key, artifact, entry);
                else if (Regex.IsMatch(Path.GetFileName(entry), @"^v\d+$"))
                    foreach (var nested in SafeEnumerate(entry, directories: true))
                        if (TryParse(Path.GetFileName(nested), out key, out artifact))
                            yield return (key, artifact, nested);
            }
        }
        var inventories = Path.Combine(directory, "inventories");
        if (Directory.Exists(inventories))
            foreach (var file in SafeEnumerate(inventories, directories: false))
                if (TryParse(Path.GetFileName(file), out var key, out var artifact))
                    yield return (key, artifact, file);
    }

    // The raw artifact archive plus its checksum record, publication lock and any abandoned temporary download.
    public static IEnumerable<string> ArtifactFiles(string directory, long artifactId)
    {
        var artifacts = Path.Combine(directory, "artifacts");
        if (!Directory.Exists(artifacts)) return [];
        return SafeEnumerate(artifacts, directories: false, pattern: artifactId + ".*").ToArray();
    }

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += file.Length; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static IEnumerable<string> SafeEnumerate(string directory, bool directories, string pattern = "*")
    {
        try
        {
            return (directories ? Directory.EnumerateDirectories(directory, pattern) : Directory.EnumerateFiles(directory, pattern))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryParse(string name, out BuildKey key, out long artifactId)
    {
        key = default;
        artifactId = 0;
        var match = BuildName().Match(name);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out var pullRequest)
            || !long.TryParse(match.Groups[2].Value, out var run)
            || !int.TryParse(match.Groups[3].Value, out var attempt)
            || !long.TryParse(match.Groups[4].Value, out artifactId)) return false;
        key = new BuildKey(pullRequest, run, attempt);
        return true;
    }

    // Best effort: anything still in use is retried by the next sweep.
    public static long Purge(string path)
    {
        long reclaimed = 0;
        try
        {
            if (File.Exists(path))
            {
                var length = SizeOf(path);
                File.Delete(path);
                return length;
            }
            if (!Directory.Exists(path)) return 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToArray())
            {
                try
                {
                    var length = file.Length;
                    file.Delete();
                    reclaimed += length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            try { Directory.Delete(path, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return reclaimed;
    }

    public static void SweepTrash(string directory)
    {
        foreach (var trash in SafeEnumerate(directory, directories: true, pattern: TrashPrefix + "*")) Purge(trash);
    }
}
