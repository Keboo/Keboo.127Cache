using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache;

// A source as edited by users; the same shape as one entry of the Sources array in a configuration file.
public sealed record SourceDefinition(
    string Name, string Repository, string Workflow, string Artifact, string? VersionArtifact = null,
    string Type = "GitHubActions")
{
    internal static SourceDefinition From(SourceOptions source) =>
        new(source.Name, source.Repository, source.Workflow, source.Artifact, source.VersionArtifact, source.Type);

    internal SourceOptions ToOptions() => new()
    {
        Name = Name, Repository = Repository, Workflow = Workflow, Artifact = Artifact, VersionArtifact = VersionArtifact,
        Type = Type
    };
}

// Where source edits are persisted: the --config file when one was given, otherwise the per-user config.json.
// A null user path (the default) means "no writable location": sources are then read-only.
public sealed record SourceConfigurationLocations(string? UserConfigPath, string? ExplicitConfigPath)
{
    public static SourceConfigurationLocations None { get; } = new(null, null);
    public string? WritablePath => ExplicitConfigPath ?? UserConfigPath;
    public string? WritableLayer => ExplicitConfigPath is not null ? "explicit" : UserConfigPath is not null ? "user" : null;
}

// A configuration layer, e.g. a JSON file or the environment variables.
public sealed record ConfigurationLayerInfo(string Kind, string Description, string? Path);

public sealed record SourceConfigurationInfo(
    string? WritableFilePath,
    string? WritableLayer,
    bool FileExists,
    // Identifies the current content of the writable file; pass it back as the expected ETag to detect external edits.
    string? ETag,
    bool IsEditable,
    string? ReadOnlyReason,
    // Higher-precedence layers that also define Sources; edits to the writable file would be ignored while they exist.
    IReadOnlyList<ConfigurationLayerInfo> ShadowingLayers,
    // Lower-precedence layers whose Sources array the first edit replaces (a Sources array is never merged).
    IReadOnlyList<ConfigurationLayerInfo> OverriddenLayers,
    // True when the files or environment on disk no longer match what the server is running.
    bool HasExternalChanges,
    // The sources the server is running now.
    IReadOnlyList<SourceDefinition> Sources);

public enum SourceChangeStatus
{
    Applied,
    // The definition failed validation; Errors lists the fields. Nothing was written.
    Invalid,
    // The named source does not exist.
    NotFound,
    // The writable file changed since the expected ETag was read.
    Conflict,
    // Sources cannot be edited here: no writable file, or a higher layer (e.g. environment variables) overrides them.
    Blocked,
    // The configuration on disk is unreadable or invalid; it must be fixed first.
    ConfigurationError,
    // Cache removal was requested but a build is in use; nothing was changed.
    CacheInUse,
    // The file could not be written.
    WriteFailed
}

public sealed record SourceChangeResult(
    SourceChangeStatus Status,
    IReadOnlyDictionary<string, string[]> Errors,
    string? Message,
    SourceDefinition? Source,
    SourceConfigurationInfo Configuration,
    SourceReconciliation? Applied,
    RemovalResult? CacheRemoval)
{
    public bool Succeeded => Status == SourceChangeStatus.Applied;
}

public sealed record SourceChangeOptions(string? ExpectedETag = null, bool RemoveCache = false);

// Adds, edits and removes sources. Every change is validated, written atomically to the writable configuration file
// (only the Sources array changes; other content is preserved) and applied to the running server without a restart.
// Changes are refused (never silently lost) when a higher-precedence layer overrides Sources.
public interface ISourceConfigurationService
{
    // Where the configuration lives and whether it can be edited. Does not read the configuration again.
    Task<SourceConfigurationInfo> GetConfigurationAsync(CancellationToken cancellationToken = default);

    // Field-level validation without persisting. replacingName excludes that source from the uniqueness check.
    // Keys of the result are SourceDefinition property names; an empty result means valid.
    Task<IReadOnlyDictionary<string, string[]>> ValidateAsync(SourceDefinition source, string? replacingName = null,
        CancellationToken cancellationToken = default);

    Task<SourceChangeResult> AddAsync(SourceDefinition source, SourceChangeOptions? options = null,
        CancellationToken cancellationToken = default);

    // Editing repository, workflow or artifacts purges the cache of the source, which would otherwise serve stale data.
    // Renaming is a remove plus an add: the cache of the old name is kept unless options.RemoveCache is set.
    Task<SourceChangeResult> UpdateAsync(string name, SourceDefinition source, SourceChangeOptions? options = null,
        CancellationToken cancellationToken = default);

    // Keeps the cache on disk unless options.RemoveCache is set, in which case it is removed first (and the removal
    // is refused while a build is in use).
    Task<SourceChangeResult> RemoveAsync(string name, SourceChangeOptions? options = null,
        CancellationToken cancellationToken = default);

    // Re-reads the configuration files and environment and applies them (for edits made outside the application).
    Task<SourceChangeResult> ReloadAsync(CancellationToken cancellationToken = default);
}

internal static class SourceValidator
{
    public static readonly string[] Fields = ["Name", "Repository", "Workflow", "Artifact", "VersionArtifact", "Type"];

    public static SourceDefinition Normalize(SourceDefinition source) => new(
        (source.Name ?? "").Trim(), (source.Repository ?? "").Trim(), (source.Workflow ?? "").Trim(),
        (source.Artifact ?? "").Trim(),
        string.IsNullOrWhiteSpace(source.VersionArtifact) ? null : source.VersionArtifact.Trim(),
        string.IsNullOrWhiteSpace(source.Type) ? "GitHubActions" : source.Type.Trim());

    // The source must already be normalized.
    public static Dictionary<string, string[]> Validate(SourceDefinition source, IEnumerable<string> otherNames)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (source.Name.Length == 0)
            errors["Name"] = ["Name is required."];
        else if (!CacheConfigurationLoader.IsSafeName(source.Name))
            errors["Name"] = ["Name must be filesystem-safe: letters, digits, '.', '-' or '_' (at most 255), no reserved names such as CON, and no trailing '.'."];
        else if (otherNames.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            errors["Name"] = [$"A source named '{source.Name}' already exists (names are case-insensitive)."];
        if (source.Repository.Length == 0)
            errors["Repository"] = ["Repository is required."];
        else if (!CacheConfigurationLoader.IsValidRepository(source.Repository))
            errors["Repository"] = ["Repository must be a GitHub owner/repository, e.g. 'dotnet/runtime'."];
        if (source.Workflow.Length == 0)
            errors["Workflow"] = ["Workflow is required."];
        else if (!CacheConfigurationLoader.IsValidWorkflow(source.Workflow))
            errors["Workflow"] = ["Workflow must be a workflow file name such as 'build.yml' (no path)."];
        if (source.Artifact.Length == 0)
            errors["Artifact"] = ["Artifact is required."];
        if (!source.Type.Equals("GitHubActions", StringComparison.OrdinalIgnoreCase))
            errors["Type"] = ["Type must be GitHubActions; other source types are not supported yet."];
        return errors;
    }
}

public sealed class SourceConfigurationService : ISourceConfigurationService, IHostedService
{
    private static readonly string[] LegacySourceFields = ["Repository", "Workflow", "PackagesArtifact", "VersionArtifact"];
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    private readonly IConfiguration _configuration;
    private readonly SourceConfigurationLocations _locations;
    private readonly PackageCatalog _catalog;
    private readonly ProxyOptions _options;
    private readonly ICacheEvictionService _eviction;
    private readonly ILogger<CacheConfiguration> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private string _appliedFingerprint;

    public SourceConfigurationService(IConfiguration configuration, SourceConfigurationLocations locations,
        PackageCatalog catalog, ProxyOptions options, ICacheEvictionService eviction, ILogger<CacheConfiguration> logger)
    {
        _configuration = configuration;
        _locations = locations;
        _catalog = catalog;
        _options = options;
        _eviction = eviction;
        _logger = logger;
        _appliedFingerprint = Fingerprint();
    }

    // Registered as a hosted service only so the baseline for external-change detection is taken when the host starts.
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SourceConfigurationInfo> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Describe());

    public Task<IReadOnlyDictionary<string, string[]>> ValidateAsync(SourceDefinition source, string? replacingName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var others = _options.ConfiguredSources.Select(x => x.Name)
            .Where(x => replacingName is null || !x.Equals(replacingName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyDictionary<string, string[]>>(
            SourceValidator.Validate(SourceValidator.Normalize(source), others));
    }

    public Task<SourceChangeResult> AddAsync(SourceDefinition source, SourceChangeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ChangeAsync(options, cancellationToken, async (sources, change) =>
        {
            var normalized = SourceValidator.Normalize(source);
            var errors = SourceValidator.Validate(normalized, sources.Select(x => x.Name));
            if (errors.Count > 0) return change.Invalid(errors);
            sources.Add(normalized.ToOptions());
            return await change.CommitAsync(sources, normalized, purgeRemoved: []);
        });
    }

    public Task<SourceChangeResult> UpdateAsync(string name, SourceDefinition source, SourceChangeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ChangeAsync(options, cancellationToken, async (sources, change) =>
        {
            var index = sources.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return change.NotFound(name);
            var existing = sources[index];
            var normalized = SourceValidator.Normalize(source);
            var errors = SourceValidator.Validate(normalized, sources.Where((_, i) => i != index).Select(x => x.Name));
            if (errors.Count > 0) return change.Invalid(errors);
            var renamed = !existing.Name.Equals(normalized.Name, StringComparison.Ordinal);
            if (renamed && options?.RemoveCache == true
                && await change.RemoveCacheAsync(existing.Name) is { } failure) return failure;
            sources[index] = normalized.ToOptions();
            return await change.CommitAsync(sources, normalized,
                purgeRemoved: renamed && options?.RemoveCache == true ? [existing.Name] : []);
        });
    }

    public Task<SourceChangeResult> RemoveAsync(string name, SourceChangeOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(options, cancellationToken, async (sources, change) =>
        {
            var index = sources.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return change.NotFound(name);
            var existing = sources[index];
            if (options?.RemoveCache == true && await change.RemoveCacheAsync(existing.Name) is { } failure) return failure;
            sources.RemoveAt(index);
            return await change.CommitAsync(sources, null, purgeRemoved: options?.RemoveCache == true ? [existing.Name] : []);
        });

    public async Task<SourceChangeResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryLoad(out var loaded, out var loadError))
                return Result(SourceChangeStatus.ConfigurationError, loadError);
            var reconciliation = await _catalog.ReconcileAsync(loaded.Sources, [], cancellationToken);
            _appliedFingerprint = Fingerprint();
            return Result(SourceChangeStatus.Applied, null, applied: reconciliation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Change(SourceConfigurationService owner, CancellationToken cancellationToken)
    {
        public SourceChangeResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
            owner.Result(SourceChangeStatus.Invalid, "The source definition is not valid.", errors: errors);

        public SourceChangeResult NotFound(string name) =>
            owner.Result(SourceChangeStatus.NotFound, $"Source '{name}' is not configured.");

        // Returns a failure result, or null once the cache was removed (or there was nothing to remove).
        public async Task<SourceChangeResult?> RemoveCacheAsync(string name)
        {
            RemovalResult removal;
            try
            {
                removal = await owner._eviction.RemoveSourceAsync(name, dryRun: false, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
            return removal.Builds.Any(x => x.Error is not null)
                ? owner.Result(SourceChangeStatus.CacheInUse,
                    "At least one cached build is in use, so nothing was changed. Try again when it is no longer in use.",
                    removal: removal)
                : null;
        }

        public async Task<SourceChangeResult> CommitAsync(
            List<SourceOptions> sources, SourceDefinition? saved, IReadOnlyCollection<string> purgeRemoved)
        {
            try
            {
                await owner.WriteAsync(sources, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return owner.Result(SourceChangeStatus.WriteFailed,
                    $"The configuration file could not be written: {exception.Message}");
            }

            if (!owner.TryLoad(out var loaded, out var error))
                return owner.Result(SourceChangeStatus.ConfigurationError, error);
            var reconciliation = await owner._catalog.ReconcileAsync(loaded.Sources, purgeRemoved, cancellationToken);
            owner._appliedFingerprint = owner.Fingerprint();
            return owner.Result(SourceChangeStatus.Applied, null, source: saved, applied: reconciliation);
        }
    }

    // Serializes changes, refuses blocked or conflicting ones, and hands the current effective sources to the change.
    private async Task<SourceChangeResult> ChangeAsync(SourceChangeOptions? options, CancellationToken cancellationToken,
        Func<List<SourceOptions>, Change, Task<SourceChangeResult>> change)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryLoad(out var loaded, out var loadError))
                return Result(SourceChangeStatus.ConfigurationError, loadError);
            var info = Describe();
            if (!info.IsEditable)
                return Result(SourceChangeStatus.Blocked, info.ReadOnlyReason);
            if (options?.ExpectedETag is { } expected && !string.Equals(Unquote(expected), info.ETag, StringComparison.Ordinal))
                return Result(SourceChangeStatus.Conflict,
                    "The configuration file changed since it was read. Review the current sources and try again.");
            return await change(loaded.Sources.Select(Clone).ToList(), new Change(this, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SourceOptions Clone(SourceOptions source) => SourceDefinition.From(source).ToOptions();

    internal static string Unquote(string etag)
    {
        etag = etag.Trim();
        if (etag.StartsWith("W/", StringComparison.Ordinal)) etag = etag[2..];
        return etag.Trim('"');
    }

    private SourceChangeResult Result(
        SourceChangeStatus status, string? message, IReadOnlyDictionary<string, string[]>? errors = null,
        SourceDefinition? source = null, SourceReconciliation? applied = null, RemovalResult? removal = null) =>
        new(status, errors ?? new Dictionary<string, string[]>(), message, source, Describe(), applied, removal);

    // Reloads every configuration provider and builds the effective configuration.
    private bool TryLoad(out CacheConfiguration loaded, out string? error)
    {
        loaded = new CacheConfiguration();
        error = null;
        if (_configuration is not IConfigurationRoot root)
        {
            error = "The configuration cannot be reloaded.";
            return false;
        }
        try
        {
            root.Reload();
            loaded = CacheConfigurationLoader.Load(root, _logger);
            return true;
        }
        catch (OptionsValidationException exception)
        {
            error = "The configuration is invalid and must be fixed first: " + string.Join(" ", exception.Failures);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException
            or UnauthorizedAccessException or JsonException)
        {
            error = "The configuration could not be read: " + exception.Message;
        }
        return false;
    }

    private SourceConfigurationInfo Describe()
    {
        var path = _locations.WritablePath is { } writable ? Path.GetFullPath(writable) : null;
        var exists = path is not null && File.Exists(path);
        var shadowing = new List<ConfigurationLayerInfo>();
        var overridden = new List<ConfigurationLayerInfo>();
        string? reason = null;
        if (path is null)
            reason = "No configuration file location is available, so sources cannot be saved.";
        else if (_configuration is IConfigurationRoot root)
        {
            var providers = root.Providers.ToList();
            var index = providers.FindIndex(x => IsFile(x, path));
            if (index < 0)
                reason = $"'{path}' is not part of the loaded configuration.";
            else
            {
                for (var i = 0; i < providers.Count; i++)
                    if (i != index && DefinesSources(providers[i]))
                        (i > index ? shadowing : overridden).Add(Layer(providers[i]));
                if (shadowing.Count > 0)
                    reason = "Sources are also set by " + string.Join(", ", shadowing.Select(x => x.Description))
                        + ", which override '" + path + "'. Edits would be ignored; remove them there instead.";
            }
        }
        else
            reason = "The configuration cannot be reloaded.";
        if (reason is null && exists && new FileInfo(path!).IsReadOnly)
            reason = $"'{path}' is read-only.";

        var sources = _options.ConfiguredSources.Select(SourceDefinition.From).ToArray();
        return new SourceConfigurationInfo(path, _locations.WritableLayer, exists, path is null ? null : ETag(path),
            reason is null, reason, shadowing.DistinctBy(x => x.Description).ToArray(),
            overridden.DistinctBy(x => x.Description).ToArray(), Fingerprint() != _appliedFingerprint, sources);
    }

    private static string ETag(string path)
    {
        try
        {
            return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
        }
        catch (IOException)
        {
            return "unreadable";
        }
    }

    // Changes whenever a configuration file or the environment variables that feed the configuration change.
    private string Fingerprint()
    {
        var parts = new List<string>();
        foreach (var path in new[] { _locations.UserConfigPath, _locations.ExplicitConfigPath }.OfType<string>())
            parts.Add(ETag(Path.GetFullPath(path)));
        foreach (var variable in Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(x => x.StartsWith("Sources", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            parts.Add(variable + "=" + Environment.GetEnvironmentVariable(variable));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }

    private static bool IsFile(IConfigurationProvider provider, string path) =>
        provider is JsonConfigurationProvider { Source: { FileProvider: PhysicalFileProvider files, Path: { } name } }
        && string.Equals(Path.GetFullPath(Path.Combine(files.Root, name)), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool DefinesSources(IConfigurationProvider provider) =>
        Defines(provider, "Sources") || LegacySourceFields.Any(field => Defines(provider, "Proxy:" + field));

    private static bool Defines(IConfigurationProvider provider, string path) =>
        provider.TryGet(path, out _) || provider.GetChildKeys([], path).Any();

    private static ConfigurationLayerInfo Layer(IConfigurationProvider provider)
    {
        if (provider is JsonConfigurationProvider { Source: { FileProvider: PhysicalFileProvider files, Path: { } name } })
        {
            var full = Path.GetFullPath(Path.Combine(files.Root, name));
            return new ConfigurationLayerInfo("file", $"the configuration file '{full}'", full);
        }
        return provider.GetType().Name.Contains("EnvironmentVariables", StringComparison.Ordinal)
            ? new ConfigurationLayerInfo("environment", "environment variables (Sources__<n>__<Field> or Proxy__<Field>)", null)
            : new ConfigurationLayerInfo("other", provider.ToString() ?? provider.GetType().Name, null);
    }

    // Rewrites only the Sources array of the writable file; other settings and their order are preserved. Comments in
    // the file are not preserved. The file is replaced atomically.
    private async Task WriteAsync(IReadOnlyList<SourceOptions> sources, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(_locations.WritablePath!);
        JsonObject root;
        if (File.Exists(path) && !string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(path, cancellationToken)))
        {
            try
            {
                root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken), documentOptions: LenientJson)
                    as JsonObject ?? throw new IOException("The configuration file must contain a JSON object.");
            }
            catch (JsonException exception)
            {
                throw new IOException("The configuration file is not valid JSON: " + exception.Message, exception);
            }
        }
        else
            root = [];

        var array = new JsonArray();
        foreach (var source in sources)
        {
            var node = new JsonObject
            {
                ["Name"] = source.Name, ["Repository"] = source.Repository, ["Workflow"] = source.Workflow,
                ["Artifact"] = source.Artifact
            };
            if (source.VersionArtifact is not null) node["VersionArtifact"] = source.VersionArtifact;
            if (!source.Type.Equals("GitHubActions", StringComparison.OrdinalIgnoreCase)) node["Type"] = source.Type;
            array.Add(node);
        }

        var key = root.Select(x => x.Key).FirstOrDefault(x => x.Equals("Sources", StringComparison.OrdinalIgnoreCase)) ?? "Sources";
        root[key] = array;

        // Legacy Proxy source fields in this file would be ignored (with a warning) next to Sources; they are migrated.
        var legacyKey = root.Select(x => x.Key).FirstOrDefault(x => x.Equals("Proxy", StringComparison.OrdinalIgnoreCase));
        if (legacyKey is not null && root[legacyKey] is JsonObject legacy)
        {
            foreach (var field in legacy.Select(x => x.Key)
                .Where(x => LegacySourceFields.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray())
                legacy.Remove(field);
            if (legacy.Count == 0) root.Remove(legacyKey);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await JsonSettings.WriteAtomicAsync(path, root, cancellationToken);
    }
}
