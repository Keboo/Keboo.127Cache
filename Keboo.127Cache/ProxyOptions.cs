using System.Text.Json;

namespace Keboo.LocalCache;

public sealed class ProxyOptions
{
    internal const int RunLimit = 100;
    public bool HasSource { get; set; } = true;
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Workflow { get; set; } = "build.yml";
    public string PackagesArtifact { get; set; } = "packages";
    public string? VersionArtifact { get; set; } = "build-version";
    // Retained for direct callers; legacy configuration values are never bound to these selectors.
    public int[] PullRequests { get => _origin?.PullRequests ?? _pullRequests; set => _pullRequests = value; }
    public long[] RunIds { get => _origin?.RunIds ?? _runIds; set => _runIds = value; }
    public string BaseUrl { get => _origin?.BaseUrl ?? _baseUrl; set => _baseUrl = value; }
    public int RefreshSeconds { get => _origin?.RefreshSeconds ?? _refreshSeconds; set => _refreshSeconds = value; }
    // Pull requests closed for at least this many days are evicted automatically; null disables the policy.
    public int? EvictClosedPullRequestsAfterDays { get; set; }
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    private int[] _pullRequests = [];
    private long[] _runIds = [];
    private string _baseUrl = "http://localhost:55631";
    private int _refreshSeconds = 120;
    // The first source observes live changes to the root's selectors, which callers adjust after startup.
    private ProxyOptions? _origin;
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Keboo.127Cache", "cache");
    // The first configured source is mirrored on this instance; the rest are only described here.
    internal IReadOnlyList<SourceOptions> ConfiguredSources { get; set; } = [];

    public static ProxyOptions FromConfiguration(CacheConfiguration configuration)
    {
        var source = configuration.Sources.FirstOrDefault();
        var options = new ProxyOptions
        {
            HasSource = source is not null,
            Name = source?.Name ?? "",
            Repository = source?.Repository ?? "",
            Workflow = source?.Workflow ?? "",
            PackagesArtifact = source?.Artifact ?? "",
            VersionArtifact = source?.VersionArtifact,
            BaseUrl = configuration.Server.BaseUrl,
            RefreshSeconds = configuration.Server.RefreshSeconds,
            EvictClosedPullRequestsAfterDays = configuration.Server.EvictClosedPullRequestsAfterDays,
            ConfiguredSources = configuration.Sources.ToArray()
        };
        if (configuration.Server.CacheDirectory is not null)
            options.CacheDirectory = configuration.Server.CacheDirectory;
        return options;
    }

    // One options instance per configured source, each rooted at {CacheDirectory}/sources/{Name}.
    internal IReadOnlyList<ProxyOptions> CreateSourceOptions()
    {
        var result = new List<ProxyOptions>();
        for (var index = 0; index < ConfiguredSources.Count; index++)
        {
            var source = ConfiguredSources[index];
            var options = index == 0
                ? new ProxyOptions
                {
                    HasSource = true,
                    Repository = Repository,
                    Workflow = Workflow,
                    PackagesArtifact = PackagesArtifact,
                    VersionArtifact = VersionArtifact,
                    _origin = this
                }
                : new ProxyOptions
                {
                    HasSource = true,
                    Repository = source.Repository,
                    Workflow = source.Workflow,
                    PackagesArtifact = source.Artifact,
                    VersionArtifact = source.VersionArtifact,
                    BaseUrl = BaseUrl,
                    RefreshSeconds = RefreshSeconds
                };
            options.Name = source.Name;
            options.TimeProvider = TimeProvider;
            options.CacheDirectory = CacheLayout.SourceDirectory(CacheDirectory, source.Name);
            result.Add(options);
        }
        return result;
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
