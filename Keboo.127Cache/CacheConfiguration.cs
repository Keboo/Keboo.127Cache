using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
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
        builder.AddJsonFile(Path.GetFullPath(userConfigPath ?? UserConfigPath()), optional: true, reloadOnChange: false);
        if (explicitConfig is not null)
            builder.AddJsonFile(explicitConfig.FullName, optional: false, reloadOnChange: false);
        builder.AddEnvironmentVariables();
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

            if (hasSources)
            {
                if (hasLegacySource) conflictingSources = true;
                if (mode != SourceMode.Modern || provider is not EnvironmentVariablesConfigurationProvider || hasSourceValue)
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
                if (mode != SourceMode.Legacy)
                    legacy = NewLegacySource();
                mode = SourceMode.Legacy;
                ApplyLegacySource(provider, legacy);
            }
        }

        if (sawLegacy)
            logger.LogWarning("The Proxy configuration section is deprecated; use Server and Sources instead.");
        foreach (var field in IgnoredLegacyFields.Where(ignoredLegacy.Contains))
            logger.LogWarning("Proxy.{Field} is ignored; the feed uses the latest successful builds of open pull requests.", field);
        if (conflictingSources)
            logger.LogWarning("Proxy source settings are ignored when Sources is supplied in the same configuration layer.");

        if (mode == SourceMode.Legacy)
        {
            legacy.Value.Name = LegacyName(legacy.Value.Repository);
            sources.Clear();
            sources[0] = legacy;
            sourceErrors.Clear();
        }

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
            if (children.Length > 0) errors.Remove("Server");
            if (provider.TryGet(section, out var value) && !string.IsNullOrEmpty(value))
                errors["Server"] = "Server must be an object.";
            foreach (var child in children.Where(child => !ServerFields.Contains(child)))
                errors[$"Server.{child}"] = $"Server.{child} is not a supported field.";
        }

        foreach (var field in ServerFields)
        {
            var path = $"{section}:{field}";
            var name = $"Server.{field}";
            if (provider.TryGet(path, out var value))
            {
                errors.Remove(name);
                switch (field)
                {
                    case "BaseUrl": server.BaseUrl = value ?? ""; break;
                    case "CacheDirectory": server.CacheDirectory = value; break;
                    case "RefreshSeconds": refreshSeconds = value ?? ""; break;
                }
            }
            else if (HasSection(provider, path))
                errors[name] = $"{name} must be a scalar value.";
        }
    }

    private static SourceState NewLegacySource() => new()
    {
        Value = new SourceOptions { Workflow = "build.yml", Artifact = "packages", VersionArtifact = "build-version" }
    };

    private static void ApplyLegacySource(IConfigurationProvider provider, SourceState state)
    {
        if (provider.TryGet("Proxy:Repository", out var repository)) state.Value.Repository = repository ?? "";
        if (provider.TryGet("Proxy:Workflow", out var workflow)) state.Value.Workflow = workflow ?? "";
        if (provider.TryGet("Proxy:PackagesArtifact", out var artifact)) state.Value.Artifact = artifact ?? "";
        if (provider.TryGet("Proxy:VersionArtifact", out var versionArtifact)) state.Value.VersionArtifact = versionArtifact;
    }

    private static void ApplySource(IConfigurationProvider provider, string path, int index, SourceState state)
    {
        if (provider.TryGet(path, out var value) && !string.IsNullOrEmpty(value))
            state.Errors["object"] = $"Sources[{index}] must be an object.";

        foreach (var field in Children(provider, path))
        {
            if (!SourceFields.Contains(field))
            {
                state.Errors[field] = $"Sources[{index}].{field} is not a supported field.";
                continue;
            }

            var key = $"{path}:{field}";
            if (!provider.TryGet(key, out var fieldValue))
            {
                state.Errors[field] = $"Sources[{index}].{field} must be a scalar value.";
                continue;
            }

            state.Errors.Remove(field);
            switch (field.ToUpperInvariant())
            {
                case "NAME": state.Value.Name = fieldValue ?? ""; break;
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

            if (!Regex.IsMatch(source.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
                || source.Repository.Split('/').Any(part => part is "." or ".."))
                failures.Add($"Sources[{index}].Repository{nameContext} must be a GitHub owner/repository.");
            if (!Regex.IsMatch(source.Workflow, @"^[A-Za-z0-9_.-]+$") || source.Workflow is "." or "..")
                failures.Add($"Sources[{index}].Workflow{nameContext} must be a workflow filename.");
            if (string.IsNullOrWhiteSpace(source.Artifact))
                failures.Add($"Sources[{index}].Artifact{nameContext} is required.");
            if (source.VersionArtifact is not null && string.IsNullOrWhiteSpace(source.VersionArtifact))
                failures.Add($"Sources[{index}].VersionArtifact{nameContext} must be non-empty when supplied.");
            if (!source.Type.Equals("GitHubActions", StringComparison.OrdinalIgnoreCase))
                failures.Add($"Sources[{index}].Type{nameContext} must be GitHubActions; other source types are not supported yet.");
        }
    }

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
        public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
