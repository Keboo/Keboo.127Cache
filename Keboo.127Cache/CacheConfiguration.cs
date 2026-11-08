using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Keboo._127Cache;

public sealed class CacheConfiguration
{
    public ServerOptions Server { get; set; } = new();
    public List<SourceOptions> Sources { get; set; } = [];
}

public sealed class ServerOptions
{
    public string BaseUrl { get; set; } = "http://localhost:55631";
    public string? CacheDirectory { get; set; }
    public int RefreshSeconds { get; set; } = 120;
}

public sealed class SourceOptions
{
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Workflow { get; set; } = "";
    public string Artifact { get; set; } = "";
    public string? VersionArtifact { get; set; }
    public string Type { get; set; } = "GitHubActions";
}

public static class ConfigurationFiles
{
    public static string UserConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(appData))
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Keboo.127Cache", "config.json");
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathFullyQualified(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "keboo-127cache", "config.json");
    }

    public static void AddUserAndExplicitConfiguration(
        IConfigurationBuilder builder, FileInfo? explicitConfig, string? userConfigPath = null)
    {
        TrackExistingJsonFiles(builder);
        AddJsonFile(builder, Path.GetFullPath(userConfigPath ?? UserConfigPath()), optional: true);
        if (explicitConfig is not null)
            AddJsonFile(builder, explicitConfig.FullName, optional: false);
        builder.AddEnvironmentVariables();
    }

    private static void TrackExistingJsonFiles(IConfigurationBuilder builder)
    {
        for (var index = 0; index < builder.Sources.Count; index++)
        {
            if (builder.Sources[index] is not JsonConfigurationSource source
                || source.GetType() != typeof(JsonConfigurationSource) || source.Path is not { } path) continue;
            builder.Sources[index] = new ShapeTrackingJsonConfigurationSource
            {
                Path = path,
                FileProvider = source.FileProvider,
                Optional = source.Optional,
                ReloadOnChange = source.ReloadOnChange,
                ReloadDelay = source.ReloadDelay,
                OnLoadException = source.OnLoadException
            };
        }
    }

    private static void AddJsonFile(IConfigurationBuilder builder, string path, bool optional)
    {
        var source = new ShapeTrackingJsonConfigurationSource { Path = path, Optional = optional };
        source.ResolveFileProvider();
        builder.Add(source);
    }
}

internal sealed class ShapeTrackingJsonConfigurationSource : JsonConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ShapeTrackingJsonConfigurationProvider(this);
    }
}

internal sealed class ShapeTrackingJsonConfigurationProvider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
{
    public JsonConfigurationShape? Shape { get; private set; }

    public override void Load(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        // Flattening otherwise makes empty arrays, objects and scalar values indistinguishable.
        var shape = JsonConfigurationShapes.Read(buffer);
        buffer.Position = 0;
        base.Load(buffer);
        Shape = shape;
    }
}

internal sealed class JsonConfigurationShape
{
    private readonly Dictionary<string, JsonValueKind> _kinds = new(StringComparer.OrdinalIgnoreCase);

    public JsonValueKind? Kind(string path) => _kinds.TryGetValue(path, out var kind) ? kind : null;

    public void Add(string path, JsonValueKind kind)
    {
        if (!_kinds.TryAdd(path, kind)) _kinds[path] = JsonValueKind.Undefined;
    }

    public void MarkInvalid(string path) => _kinds[path] = JsonValueKind.Undefined;
}

internal static class JsonConfigurationShapes
{
    public static bool SourcesAreArray(IConfigurationProvider provider)
    {
        var shape = FromProvider(provider);
        return shape is not null
            ? shape.Kind("Sources") == JsonValueKind.Array
            : provider is not (JsonConfigurationProvider or JsonStreamConfigurationProvider);
    }

    public static JsonValueKind? Kind(IConfigurationProvider provider, string path) =>
        FromProvider(provider)?.Kind(path);

    private static JsonConfigurationShape? FromProvider(IConfigurationProvider provider)
    {
        try
        {
            if (provider is ShapeTrackingJsonConfigurationProvider tracked)
                return tracked.Shape;
            if (provider is JsonConfigurationProvider file)
            {
                var info = file.Source.FileProvider?.GetFileInfo(file.Source.Path ?? "");
                if (info is null || !info.Exists) return null;
                using var stream = info.CreateReadStream();
                return Read(stream);
            }
            if (provider is JsonStreamConfigurationProvider jsonStream)
            {
                if (jsonStream.Source.Stream is MemoryStream memory)
                {
                    using var stream = new MemoryStream(memory.ToArray());
                    return Read(stream);
                }
                if (jsonStream.Source.Stream is FileStream fileStream)
                {
                    using var stream = File.OpenRead(fileStream.Name);
                    return Read(stream);
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            return null;
        }
    }

    public static JsonConfigurationShape Read(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var shape = new JsonConfigurationShape();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return shape;
        var invalidSources = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("Sources:", StringComparison.OrdinalIgnoreCase))
                invalidSources = true;
            AddElement(shape, property.Name, property.Value);
        }
        if (invalidSources) shape.MarkInvalid("Sources");
        return shape;
    }

    private static void AddElement(JsonConfigurationShape shape, string path, JsonElement element)
    {
        shape.Add(path, element.ValueKind);
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                AddElement(shape, $"{path}:{property.Name}", property.Value);
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                AddElement(shape, $"{path}:{(index++).ToString(CultureInfo.InvariantCulture)}", item);
        }
    }
}

public static class CacheConfigurationLoader
{
    private static readonly string[] LegacySourceFields =
        ["Repository", "Workflow", "PackagesArtifact", "VersionArtifact"];
    private static readonly string[] IgnoredLegacyFields = ["PullRequests", "RunIds", "RunLimit"];
    private static readonly HashSet<string> ServerFields = new(StringComparer.OrdinalIgnoreCase)
        { "BaseUrl", "CacheDirectory", "RefreshSeconds" };
    private static readonly HashSet<string> SourceFields = new(StringComparer.OrdinalIgnoreCase)
        { "Name", "Repository", "Workflow", "Artifact", "VersionArtifact", "Type" };

    public static CacheConfiguration Load(IConfiguration configuration, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (configuration is not IConfigurationRoot root)
            throw new ArgumentException("Configuration must be a configuration root.", nameof(configuration));

        var server = new ServerOptions();
        var refreshSeconds = server.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        var sources = new SortedDictionary<int, SourceState>();
        var serverErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ignoredLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mode = SourceMode.None;
        var legacy = NewLegacySource();
        var sawLegacy = false;
        var conflictingSources = false;
        var combinedEnvironmentSources = false;

        foreach (var provider in root.Providers)
        {
            var hasLegacy = HasSection(provider, "Proxy");
            sawLegacy |= hasLegacy;
            foreach (var field in IgnoredLegacyFields)
                if (HasSection(provider, $"Proxy:{field}")) ignoredLegacy.Add(field);

            ApplyServer(provider, "Proxy", server, ref refreshSeconds, serverErrors);
            ApplyServer(provider, "Server", server, ref refreshSeconds, serverErrors);

            var sourceChildren = Children(provider, "Sources").ToArray();
            var hasSourceValue = provider.TryGet("Sources", out var sourceValue);
            var hasSources = hasSourceValue || sourceChildren.Length > 0;
            var hasLegacySource = LegacySourceFields.Any(field => HasSection(provider, $"Proxy:{field}"));
            var isEnvironment = provider is EnvironmentVariablesConfigurationProvider;

            if (hasSources)
            {
                if (hasLegacySource)
                {
                    if (isEnvironment && !hasSourceValue) combinedEnvironmentSources = true;
                    else conflictingSources = true;
                }
                if (!JsonConfigurationShapes.SourcesAreArray(provider))
                {
                    sourceErrors["Sources"] = "Sources must be an array.";
                    continue;
                }

                var indexedEnvironment = isEnvironment && !hasSourceValue;
                if (indexedEnvironment)
                {
                    if (mode == SourceMode.Legacy)
                    {
                        sources.Clear();
                        sources[0] = legacy;
                    }
                    if (hasLegacySource)
                    {
                        if (!sources.TryGetValue(0, out var state))
                            sources[0] = state = NewLegacySource();
                        ApplyLegacySource(provider, state);
                    }
                }
                else
                {
                    sources.Clear();
                    sourceErrors.Clear();
                }
                mode = SourceMode.Modern;
                if (hasSourceValue && sourceValue is null)
                    sourceErrors["Sources"] = "Sources must be an array; use [] for an empty list.";
                else if (hasSourceValue && sourceValue!.Length > 0)
                    sourceErrors["Sources"] = "Sources must be an array.";

                foreach (var child in sourceChildren)
                {
                    if (!int.TryParse(child, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
                    {
                        sourceErrors[$"Sources:{child}"] = $"Sources.{child} is not a valid array index.";
                        continue;
                    }

                    if (!sources.TryGetValue(index, out var state))
                        sources[index] = state = new SourceState();
                    ApplySource(provider, $"Sources:{child}", index, state);
                }
            }
            else if (hasLegacySource || (hasLegacy && mode == SourceMode.None))
            {
                if (isEnvironment && hasLegacySource && mode == SourceMode.Modern)
                {
                    if (!sources.TryGetValue(0, out var state))
                        sources[0] = state = NewLegacySource();
                    ApplyLegacySource(provider, state);
                }
                else
                {
                    if (mode != SourceMode.Legacy)
                        legacy = NewLegacySource();
                    mode = SourceMode.Legacy;
                    ApplyLegacySource(provider, legacy);
                    if (!isEnvironment && IsCompleteLegacySource(provider, legacy))
                        sourceErrors.Clear();
                }
            }
        }

        if (sawLegacy)
            logger.LogWarning("The Proxy configuration section is deprecated; use Server and Sources instead.");
        foreach (var field in IgnoredLegacyFields.Where(ignoredLegacy.Contains))
            logger.LogWarning("Proxy.{Field} is ignored; the feed uses the latest successful builds of open pull requests.", field);
        if (conflictingSources)
            logger.LogWarning("Proxy source settings are ignored when Sources is supplied in the same configuration layer.");
        if (combinedEnvironmentSources)
            logger.LogWarning("Proxy and Sources environment source settings were combined; indexed Sources fields take precedence when both set the same field.");

        if (mode == SourceMode.Legacy)
        {
            sources.Clear();
            sources[0] = legacy;
        }
        foreach (var state in sources.Values.Where(state => state.DeriveName))
            state.Value.Name = LegacyName(state.Value.Repository);

        var failures = new List<string>(serverErrors.Values);
        failures.AddRange(sourceErrors.Values);
        if (!int.TryParse(refreshSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var refresh))
            failures.Add("Server.RefreshSeconds must be an integer of at least 10.");
        else
            server.RefreshSeconds = refresh;

        ValidateServer(server, failures);
        ValidateSources(sources, failures);
        if (failures.Count > 0)
            throw new OptionsValidationException(nameof(CacheConfiguration), typeof(CacheConfiguration), failures);

        return new CacheConfiguration { Server = server, Sources = sources.Values.Select(x => x.Value).ToList() };
    }

    private static void ApplyServer(
        IConfigurationProvider provider, string section, ServerOptions server, ref string refreshSeconds,
        Dictionary<string, string> errors)
    {
        var children = Children(provider, section).ToArray();
        if (section == "Server")
        {
            var kind = HasSection(provider, section) ? JsonConfigurationShapes.Kind(provider, section) : null;
            if (kind == JsonValueKind.Object)
                errors.Remove("Server");
            else if (kind is not null || (provider.TryGet(section, out var value) && value is not null))
                errors["Server"] = "Server must be an object.";
            foreach (var child in children.Where(child => !ServerFields.Contains(child)))
                errors[$"Server.{child}"] = $"Server.{child} is not a supported field.";
        }

        foreach (var field in ServerFields)
        {
            var path = $"{section}:{field}";
            var name = $"Server.{field}";
            if (!HasSection(provider, path)) continue;
            if (IsNonScalar(provider, path) || !provider.TryGet(path, out var value))
                errors[name] = $"{name} must be a scalar value.";
            else
            {
                errors.Remove(name);
                switch (field)
                {
                    case "BaseUrl": server.BaseUrl = value ?? ""; break;
                    case "CacheDirectory": server.CacheDirectory = value; break;
                    case "RefreshSeconds": refreshSeconds = value ?? ""; break;
                }
            }
        }
    }

    private static SourceState NewLegacySource() => new()
    {
        Value = new SourceOptions { Workflow = "build.yml", Artifact = "packages", VersionArtifact = "build-version" },
        DeriveName = true
    };

    private static void ApplyLegacySource(IConfigurationProvider provider, SourceState state)
    {
        foreach (var field in LegacySourceFields)
        {
            var path = $"Proxy:{field}";
            if (!HasSection(provider, path)) continue;
            var sourceField = field == "PackagesArtifact" ? "Artifact" : field;
            if (IsNonScalar(provider, path) || !provider.TryGet(path, out var value))
            {
                state.Errors[sourceField] = $"Proxy.{field} must be a scalar value.";
                continue;
            }

            state.Errors.Remove(sourceField);
            switch (field)
            {
                case "Repository": state.Value.Repository = value ?? ""; break;
                case "Workflow": state.Value.Workflow = value ?? ""; break;
                case "PackagesArtifact": state.Value.Artifact = value ?? ""; break;
                case "VersionArtifact": state.Value.VersionArtifact = value; break;
            }
        }
    }

    private static bool IsCompleteLegacySource(IConfigurationProvider provider, SourceState state) =>
        provider.TryGet("Proxy:Repository", out _) && !IsNonScalar(provider, "Proxy:Repository")
        && state.Errors.Count == 0 && IsValidRepository(state.Value.Repository) && IsValidWorkflow(state.Value.Workflow)
        && !string.IsNullOrWhiteSpace(state.Value.Artifact)
        && (state.Value.VersionArtifact is null || !string.IsNullOrWhiteSpace(state.Value.VersionArtifact));

    private static void ApplySource(IConfigurationProvider provider, string path, int index, SourceState state)
    {
        var kind = JsonConfigurationShapes.Kind(provider, path);
        if ((kind is not null && kind != JsonValueKind.Object)
            || (kind is null && provider.TryGet(path, out var value) && !string.IsNullOrEmpty(value)))
            state.Errors["object"] = $"Sources[{index}] must be an object.";

        foreach (var field in Children(provider, path))
        {
            if (!SourceFields.Contains(field))
            {
                state.Errors[field] = $"Sources[{index}].{field} is not a supported field.";
                continue;
            }

            var key = $"{path}:{field}";
            if (IsNonScalar(provider, key) || !provider.TryGet(key, out var fieldValue))
            {
                state.Errors[field] = $"Sources[{index}].{field} must be a scalar value.";
                continue;
            }

            state.Errors.Remove(field);
            switch (field.ToUpperInvariant())
            {
                case "NAME": state.Value.Name = fieldValue ?? ""; state.DeriveName = false; break;
                case "REPOSITORY": state.Value.Repository = fieldValue ?? ""; break;
                case "WORKFLOW": state.Value.Workflow = fieldValue ?? ""; break;
                case "ARTIFACT": state.Value.Artifact = fieldValue ?? ""; break;
                case "VERSIONARTIFACT": state.Value.VersionArtifact = fieldValue; break;
                case "TYPE": state.Value.Type = fieldValue ?? ""; break;
            }
        }
    }

    private static IEnumerable<string> Children(IConfigurationProvider provider, string path) =>
        provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool HasSection(IConfigurationProvider provider, string path) =>
        provider.TryGet(path, out _) || Children(provider, path).Any();

    private static bool IsNonScalar(IConfigurationProvider provider, string path) =>
        JsonConfigurationShapes.Kind(provider, path) is JsonValueKind.Object or JsonValueKind.Array
        || Children(provider, path).Any();

    private static void ValidateServer(ServerOptions server, List<string> failures)
    {
        if (!Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var url) || !url.IsLoopback
            || url.Scheme is not ("http" or "https") || url.AbsolutePath != "/" || url.Query.Length != 0
            || url.Fragment.Length != 0 || server.BaseUrl.EndsWith('/'))
            failures.Add("Server.BaseUrl must be an HTTP(S) loopback origin without a trailing slash, path, query, or fragment.");
        if (server.RefreshSeconds < 10)
            failures.Add("Server.RefreshSeconds must be at least 10.");
        if (server.CacheDirectory is not null && !IsValidPath(server.CacheDirectory))
            failures.Add("Server.CacheDirectory must be a valid, non-empty path or null for the default.");
    }

    private static void ValidateSources(SortedDictionary<int, SourceState> sources, List<string> failures)
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, state) in sources)
        {
            failures.AddRange(state.Errors.Values);
            var source = state.Value;
            var nameContext = IsSafeName(source.Name) ? $" (source '{source.Name}')" : "";
            if (!IsSafeName(source.Name))
                failures.Add($"Sources[{index}].Name must be a filesystem-safe name (letters, digits, '.', '-' or '_'; no reserved names or trailing '.').");
            else if (!names.TryAdd(source.Name, index))
                failures.Add($"Sources[{index}].Name duplicates Sources[{names[source.Name]}].Name (case-insensitive).");

            if (!IsValidRepository(source.Repository))
                failures.Add($"Sources[{index}].Repository{nameContext} must be a GitHub owner/repository.");
            if (!IsValidWorkflow(source.Workflow))
                failures.Add($"Sources[{index}].Workflow{nameContext} must be a workflow filename.");
            if (string.IsNullOrWhiteSpace(source.Artifact))
                failures.Add($"Sources[{index}].Artifact{nameContext} is required.");
            if (source.VersionArtifact is not null && string.IsNullOrWhiteSpace(source.VersionArtifact))
                failures.Add($"Sources[{index}].VersionArtifact{nameContext} must be non-empty when supplied.");
            if (!source.Type.Equals("GitHubActions", StringComparison.OrdinalIgnoreCase))
                failures.Add($"Sources[{index}].Type{nameContext} must be GitHubActions; other source types are not supported yet.");
        }
    }

    private static bool IsValidRepository(string repository) =>
        Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
        && repository.Split('/').All(part => part is not ("." or ".."));

    private static bool IsValidWorkflow(string workflow) =>
        Regex.IsMatch(workflow, @"^[A-Za-z0-9_.-]+$") && workflow is not ("." or "..");

    private static bool IsSafeName(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,255}$")
        && name is not ("." or "..")
        && !name.EndsWith('.')
        && !Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase);

    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath) ?? "";
            return fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .All(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string LegacyName(string repository)
    {
        var name = repository.Split('/').Last();
        if (IsSafeName(name)) return name;
        name = Regex.Replace(name, @"[^A-Za-z0-9._-]", "-").TrimEnd('.');
        if (name.Length > 250) name = name[..250].TrimEnd('.');
        return "repo-" + name;
    }

    private enum SourceMode { None, Modern, Legacy }

    private sealed class SourceState
    {
        public SourceOptions Value { get; set; } = new();
        public bool DeriveName { get; set; }
        public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}