using System.Text.Json;

namespace Keboo._127Cache;

public sealed class ProxyOptions
{
    internal const int RunLimit = 100;
    public bool HasSource { get; set; } = true;
    public string Repository { get; set; } = "";
    public string Workflow { get; set; } = "build.yml";
    public string PackagesArtifact { get; set; } = "packages";
    public string? VersionArtifact { get; set; } = "build-version";
    // Retained for direct callers; legacy configuration values are never bound to these selectors.
    public int[] PullRequests { get; set; } = [];
    public long[] RunIds { get; set; } = [];
    public string BaseUrl { get; set; } = "http://localhost:55631";
    public int RefreshSeconds { get; set; } = 120;
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Keboo.127Cache", "cache");

    public static ProxyOptions FromConfiguration(CacheConfiguration configuration)
    {
        if (configuration.Sources.Count > 1)
            throw new NotSupportedException(
                "Multiple Sources are valid configuration, but serving more than one source requires issue #9. Configure one source until it is implemented.");

        var source = configuration.Sources.SingleOrDefault();
        var options = new ProxyOptions
        {
            HasSource = source is not null,
            Repository = source?.Repository ?? "",
            Workflow = source?.Workflow ?? "",
            PackagesArtifact = source?.Artifact ?? "",
            VersionArtifact = source?.VersionArtifact,
            BaseUrl = configuration.Server.BaseUrl,
            RefreshSeconds = configuration.Server.RefreshSeconds
        };
        if (configuration.Server.CacheDirectory is not null)
            options.CacheDirectory = configuration.Server.CacheDirectory;
        return options;
    }
}

public static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Options), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
