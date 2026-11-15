using System.Text.Json;

namespace Keboo.LocalCache;

internal static class CacheLayout
{
    private static readonly string[] LegacyEntries =
        ["catalog.json", "runs.json", "visible-runs.json", "incomplete-discovery.json", "artifacts", "packages", "inventories"];

    public static string SourceDirectory(string root, string name) => Path.Combine(root, "sources", name);

    // Moves a pre-#9 single-source cache into the directory of the one configured source it belongs to.
    // Anything that cannot be attributed is left untouched and reported, never deleted.
    public static void MigrateLegacy(string root, IReadOnlyList<ProxyOptions> sources, ILogger logger)
    {
        if (!Directory.Exists(root)) return;
        var present = LegacyEntries.Where(name => File.Exists(Path.Combine(root, name))
            || Directory.Exists(Path.Combine(root, name))).ToArray();
        if (present.Length == 0) return;

        var (repository, workflow) = ReadOwner(root);
        var candidates = sources.Where(source =>
            (repository is null || string.Equals(source.Repository, repository, StringComparison.Ordinal))
            && (workflow is null || string.Equals(source.Workflow, workflow, StringComparison.Ordinal))).ToArray();
        var target = candidates.Length == 1 && (repository is not null || sources.Count == 1) ? candidates[0] : null;
        if (target is null)
        {
            logger.LogWarning(
                "Cache directory '{Root}' contains a single-source cache from an earlier version (repository '{Repository}', "
                + "workflow '{Workflow}') that does not belong to exactly one configured source. It was left untouched and is "
                + "ignored; add a matching source or move it to '{Layout}' manually.",
                root, repository ?? "unknown", workflow ?? "unknown", Path.Combine(root, "sources", "<source name>"));
            return;
        }

        Directory.CreateDirectory(target.CacheDirectory);
        foreach (var name in present)
        {
            var from = Path.Combine(root, name);
            var to = Path.Combine(target.CacheDirectory, name);
            if (File.Exists(to) || Directory.Exists(to))
            {
                logger.LogWarning("Cannot migrate legacy cache entry '{From}': '{To}' already exists.", from, to);
                continue;
            }
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
        logger.LogInformation("Migrated the single-source cache in '{Root}' to source '{Source}' ({Directory}).",
            root, target.Name, target.CacheDirectory);
    }

    private static (string? Repository, string? Workflow) ReadOwner(string root)
    {
        foreach (var file in new[] { "runs.json", "visible-runs.json", "catalog.json" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                string? Read(string property) => document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                var repository = Read("repository");
                if (repository is not null) return (repository, Read("workflow"));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return (null, null);
    }
}
