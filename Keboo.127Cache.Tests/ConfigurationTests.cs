using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keboo._127Cache.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(
        "artifacts", "configuration-tests", Guid.NewGuid().ToString("N")));

    [Test]
    public async Task DefaultsStartWithNoRepositoryAndAUsableLoopbackServer()
    {
        var result = CacheConfigurationLoader.Load(new ConfigurationBuilder().Build());

        await Assert.That(result.Sources).IsEmpty();
        await Assert.That(result.Server.BaseUrl).IsEqualTo("http://localhost:55631");
        await Assert.That(result.Server.RefreshSeconds).IsEqualTo(120);
        await Assert.That(result.Server.CacheDirectory).IsNull();
        await Assert.That(ProxyOptions.FromConfiguration(result).HasSource).IsFalse();
    }

    [Test]
    public async Task LoadsIndependentSourcesWithoutRepositoryDefaults()
    {
        var config = Json("""
            {
              "Server": { "BaseUrl": "http://localhost:55633", "RefreshSeconds": 45 },
              "Sources": [
                { "Name": "first", "Repository": "org/one", "Workflow": "build.yml", "Artifact": "packages" },
                { "Name": "second", "Type": "GitHubActions", "Repository": "other/two",
                  "Workflow": "release.yml", "Artifact": "nupkgs", "VersionArtifact": "version" }
              ]
            }
            """);

        var result = CacheConfigurationLoader.Load(config);

        await Assert.That(result.Server.BaseUrl).IsEqualTo("http://localhost:55633");
        await Assert.That(result.Server.RefreshSeconds).IsEqualTo(45);
        await Assert.That(result.Sources.Count).IsEqualTo(2);
        await Assert.That(result.Sources[0].Name).IsEqualTo("first");
        await Assert.That(result.Sources[0].Repository).IsEqualTo("org/one");
        await Assert.That(result.Sources[0].Workflow).IsEqualTo("build.yml");
        await Assert.That(result.Sources[0].Artifact).IsEqualTo("packages");
        await Assert.That(result.Sources[0].Type).IsEqualTo("GitHubActions");
        await Assert.That(result.Sources[0].VersionArtifact).IsNull();
        await Assert.That(result.Sources[1].Name).IsEqualTo("second");
        await Assert.That(result.Sources[1].Repository).IsEqualTo("other/two");
        await Assert.That(result.Sources[1].Workflow).IsEqualTo("release.yml");
        await Assert.That(result.Sources[1].Artifact).IsEqualTo("nupkgs");
        await Assert.That(result.Sources[1].VersionArtifact).IsEqualTo("version");
    }

    [Test]
    public async Task MultiSourceConfigurationIsValidButCannotBeServedBeforeAggregationExists()
    {
        var config = Json("""
            { "Sources": [
              { "Name": "one", "Repository": "org/one", "Workflow": "a.yml", "Artifact": "a" },
              { "Name": "two", "Repository": "org/two", "Workflow": "b.yml", "Artifact": "b" }
            ] }
            """);
        using var services = new ServiceCollection().AddProxy(config).BuildServiceProvider();

        var options = services.GetRequiredService<CacheConfiguration>();
        await Assert.That(options.Sources.Count).IsEqualTo(2);
        NotSupportedException? error = null;
        try { services.GetRequiredService<ProxyOptions>(); }
        catch (NotSupportedException caught) { error = caught; }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message).Contains("Multiple Sources");
        await Assert.That(error.Message).Contains("#9");
    }

    [Test]
    [Arguments("Name")]
    [Arguments("Repository")]
    [Arguments("Workflow")]
    [Arguments("Artifact")]
    public async Task MissingRequiredFieldsIdentifyTheSourceAndField(string missing)
    {
        var entries = new Dictionary<string, string?>
        {
            ["Sources:0:Name"] = "one",
            ["Sources:0:Repository"] = "org/one",
            ["Sources:0:Workflow"] = "a.yml",
            ["Sources:0:Artifact"] = "a",
            ["Sources:1:Name"] = "two",
            ["Sources:1:Repository"] = "org/two",
            ["Sources:1:Workflow"] = "b.yml",
            ["Sources:1:Artifact"] = "b"
        };
        entries.Remove($"Sources:1:{missing}");

        var error = Invalid(new ConfigurationBuilder().AddInMemoryCollection(entries).Build());

        await Assert.That(error.Failures.Any(x => x.Contains($"Sources[1].{missing}", StringComparison.Ordinal))).IsTrue();
        await Assert.That(error.Failures.Any(x => x.Contains($"Sources[0].{missing}", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("../escape")]
    [Arguments("folder\\item")]
    [Arguments("with space")]
    [Arguments("trailing.")]
    [Arguments("CON")]
    [Arguments("nul.txt")]
    [Arguments("COM1.log")]
    [Arguments("lPt9")]
    public async Task UnsafeSourceNamesAreRejectedOnEveryPlatform(string name)
    {
        var entries = ValidSource();
        entries["Sources:0:Name"] = name;

        var error = Invalid(new ConfigurationBuilder().AddInMemoryCollection(entries).Build());

        await Assert.That(error.Failures.Any(x => x.Contains("Sources[0].Name", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("_cache")]
    [Arguments("-preview")]
    [Arguments(".hidden")]
    public async Task FilesystemSafeSourceNamesRemainValid(string name)
    {
        var entries = ValidSource();
        entries["Sources:0:Name"] = name;

        var result = CacheConfigurationLoader.Load(new ConfigurationBuilder().AddInMemoryCollection(entries).Build());

        await Assert.That(result.Sources.Single().Name).IsEqualTo(name);
    }

    [Test]
    public async Task DuplicateSourceNamesAreCaseInsensitive()
    {
        var config = Json("""
            { "Sources": [
              { "Name": "Builds", "Repository": "org/one", "Workflow": "a.yml", "Artifact": "a" },
              { "Name": "builds", "Repository": "org/two", "Workflow": "b.yml", "Artifact": "b" }
            ] }
            """);

        var error = Invalid(config);

        await Assert.That(error.Message).Contains("Sources[1].Name");
        await Assert.That(error.Message).Contains("Sources[0].Name");
        await Assert.That(error.Message).Contains("case-insensitive");
    }

    [Test]
    [Arguments("Repository", "not-a-repository")]
    [Arguments("Workflow", "../build.yml")]
    [Arguments("Artifact", " ")]
    [Arguments("VersionArtifact", " ")]
    [Arguments("Type", "AzureDevOps")]
    public async Task InvalidSourceValuesNameTheirFields(string field, string value)
    {
        var entries = ValidSource();
        entries[$"Sources:0:{field}"] = value;

        var error = Invalid(new ConfigurationBuilder().AddInMemoryCollection(entries).Build());

        await Assert.That(error.Message).Contains($"Sources[0].{field}");
    }

    [Test]
    public async Task ValidationReportsEveryInvalidServerAndSourceField()
    {
        var config = Json("""
            { "Server": { "BaseUrl": "http://example.org", "CacheDirectory": " ", "RefreshSeconds": "not-an-integer" },
              "Sources": [{ "Name": "../bad", "Repository": "bad", "Workflow": "../bad", "Artifact": "" }] }
            """);

        var error = Invalid(config);

        foreach (var field in new[] { "Server.BaseUrl", "Server.CacheDirectory", "Server.RefreshSeconds",
            "Sources[0].Name", "Sources[0].Repository", "Sources[0].Workflow", "Sources[0].Artifact" })
            await Assert.That(error.Message).Contains(field);
    }

    [Test]
    public async Task InvalidServerShapesAndPathsIdentifyTheirFields()
    {
        var invalidShape = Invalid(Json("""{ "Server": { "RefreshSeconds": { "value": 20 }, "Typo": true } }"""));
        await Assert.That(invalidShape.Message).Contains("Server.RefreshSeconds");
        await Assert.That(invalidShape.Message).Contains("Server.Typo");

        var invalidPath = Invalid(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = "bad\0path"
        }).Build());
        await Assert.That(invalidPath.Message).Contains("Server.CacheDirectory");
    }

    [Test]
    [Arguments("null")]
    [Arguments("\"not-an-array\"")]
    public async Task SourcesMustBeAnArrayRatherThanNullOrAScalar(string value)
    {
        var error = Invalid(Json($$"""{ "Sources": {{value}} }"""));

        await Assert.That(error.Message).Contains("Sources must be an array");
    }

    [Test]
    public async Task UnknownNewSourceFieldsAreNotSilentlyTreatedAsLegacyFilters()
    {
        var config = Json("""
            { "Sources": [{ "Name": "one", "Repository": "org/one", "Workflow": "build.yml",
              "Artifact": "packages", "RunIds": [123] }] }
            """);

        var error = Invalid(config);

        await Assert.That(error.Message).Contains("Sources[0].RunIds");
        await Assert.That(error.Message).Contains("not a supported field");
    }

    [Test]
    public async Task LegacyProxyMapsToOneSourceAndWarnsAboutEachIgnoredFilter()
    {
        var logger = new RecordingLogger();
        var config = Json("""
            { "Proxy": {
                "Repository": "org/widgets", "Workflow": "ci.yml", "PackagesArtifact": "nupkgs",
                "VersionArtifact": "versions", "BaseUrl": "http://localhost:55634", "RefreshSeconds": 31,
                "CacheDirectory": "legacy-cache", "PullRequests": [123], "RunIds": [456], "RunLimit": "invalid"
              } }
            """);

        var result = CacheConfigurationLoader.Load(config, logger);
        var runtime = ProxyOptions.FromConfiguration(result);

        await Assert.That(result.Sources.Count).IsEqualTo(1);
        var source = result.Sources[0];
        await Assert.That(source.Name).IsEqualTo("widgets");
        await Assert.That(source.Repository).IsEqualTo("org/widgets");
        await Assert.That(source.Workflow).IsEqualTo("ci.yml");
        await Assert.That(source.Artifact).IsEqualTo("nupkgs");
        await Assert.That(source.VersionArtifact).IsEqualTo("versions");
        await Assert.That(result.Server.BaseUrl).IsEqualTo("http://localhost:55634");
        await Assert.That(result.Server.RefreshSeconds).IsEqualTo(31);
        await Assert.That(result.Server.CacheDirectory).IsEqualTo("legacy-cache");
        await Assert.That(runtime.PullRequests).IsEmpty();
        await Assert.That(runtime.RunIds).IsEmpty();
        await Assert.That(logger.Messages.Any(x => x.Contains("deprecated", StringComparison.OrdinalIgnoreCase))).IsTrue();
        foreach (var field in new[] { "PullRequests", "RunIds", "RunLimit" })
            await Assert.That(logger.Messages.Any(x => x.Contains($"Proxy.{field}", StringComparison.Ordinal)
                && x.Contains("ignored", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task LegacyDefaultsDoNotRestoreARepositoryAndDerivedNamesAreSafe()
    {
        var error = Invalid(Json("""{ "Proxy": { "Workflow": "ci.yml" } }"""));
        await Assert.That(error.Message).Contains("Sources[0].Repository");

        var result = CacheConfigurationLoader.Load(Json("""{ "Proxy": { "Repository": "org/CON" } }"""));
        await Assert.That(result.Sources[0].Name).IsEqualTo("repo-CON");
        await Assert.That(result.Sources[0].Workflow).IsEqualTo("build.yml");
        await Assert.That(result.Sources[0].Artifact).IsEqualTo("packages");
        await Assert.That(result.Sources[0].VersionArtifact).IsEqualTo("build-version");
    }

    [Test]
    public async Task ModernSourcesTakePriorityOverLegacySourcesInTheSameLayer()
    {
        var logger = new RecordingLogger();
        var config = Json("""
            { "Proxy": { "Repository": "legacy/old" },
              "Sources": [{ "Name": "new", "Repository": "modern/new", "Workflow": "ci.yml", "Artifact": "p" }] }
            """);

        var result = CacheConfigurationLoader.Load(config, logger);

        await Assert.That(result.Sources.Single().Repository).IsEqualTo("modern/new");
        await Assert.That(logger.Messages.Any(x => x.Contains("ignored", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task ConfigurationPrecedenceIsDefaultsThenUserThenExplicitFileThenEnvironment()
    {
        Directory.CreateDirectory(_directory);
        var defaults = WriteJson("defaults.json", """
            { "Server": { "BaseUrl": "http://localhost:55100", "RefreshSeconds": 11 },
              "Sources": [{ "Name": "default", "Repository": "org/default", "Workflow": "default.yml", "Artifact": "default" }] }
            """);
        var user = WriteJson("user.json", """
            { "Server": { "BaseUrl": "http://localhost:55101", "RefreshSeconds": 12 },
              "Sources": [{ "Name": "user", "Repository": "org/user", "Workflow": "user.yml", "Artifact": "user" }] }
            """);
        var explicitFile = WriteJson("explicit.json", """
            { "Server": { "BaseUrl": "http://localhost:55102" },
              "Sources": [{ "Name": "explicit", "Repository": "org/explicit", "Workflow": "explicit.yml", "Artifact": "explicit" }] }
            """);
        var environmentNames = new[] { "Server__BaseUrl", "Server__RefreshSeconds", "Sources__0__Artifact" };
        var saved = environmentNames.ToDictionary(x => x, x => Environment.GetEnvironmentVariable(x));
        try
        {
            foreach (var name in environmentNames) Environment.SetEnvironmentVariable(name, null);
            var fromDefaults = LoadFiles(defaults, Path.Combine(_directory, "missing.json"), null);
            await Assert.That(fromDefaults.Server.BaseUrl).IsEqualTo("http://localhost:55100");
            await Assert.That(fromDefaults.Sources.Single().Repository).IsEqualTo("org/default");

            var fromUser = LoadFiles(defaults, user, null);
            await Assert.That(fromUser.Server.BaseUrl).IsEqualTo("http://localhost:55101");
            await Assert.That(fromUser.Sources.Single().Repository).IsEqualTo("org/user");

            var fromExplicit = LoadFiles(defaults, user, explicitFile);
            await Assert.That(fromExplicit.Server.BaseUrl).IsEqualTo("http://localhost:55102");
            await Assert.That(fromExplicit.Server.RefreshSeconds).IsEqualTo(12);
            await Assert.That(fromExplicit.Sources.Single().Repository).IsEqualTo("org/explicit");

            Environment.SetEnvironmentVariable("Server__BaseUrl", "http://localhost:55103");
            Environment.SetEnvironmentVariable("Server__RefreshSeconds", "13");
            Environment.SetEnvironmentVariable("Sources__0__Artifact", "environment");
            var fromEnvironment = LoadFiles(defaults, user, explicitFile);
            await Assert.That(fromEnvironment.Server.BaseUrl).IsEqualTo("http://localhost:55103");
            await Assert.That(fromEnvironment.Server.RefreshSeconds).IsEqualTo(13);
            await Assert.That(fromEnvironment.Sources.Single().Artifact).IsEqualTo("environment");
            await Assert.That(fromEnvironment.Sources.Single().Workflow).IsEqualTo("explicit.yml");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Test]
    public async Task ExplicitEmptySourcesReplacesAnInheritedSourceList()
    {
        Directory.CreateDirectory(_directory);
        var defaults = WriteJson("defaults.json", "{}");
        var user = WriteJson("user.json", """
            { "Sources": [{ "Name": "old", "Repository": "org/old", "Workflow": "ci.yml", "Artifact": "p" }] }
            """);
        var explicitFile = WriteJson("empty.json", """{ "Sources": [] }""");

        var result = LoadFiles(defaults, user, explicitFile);

        await Assert.That(result.Sources).IsEmpty();
    }

    [Test]
    public async Task AShorterSourcesArrayDoesNotRetainEarlierIndexes()
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonStream(Stream("""
            { "Sources": [
              { "Name": "one", "Repository": "org/one", "Workflow": "a.yml", "Artifact": "a" },
              { "Name": "two", "Repository": "org/two", "Workflow": "b.yml", "Artifact": "b" }
            ] }
            """));
        builder.AddJsonStream(Stream("""
            { "Sources": [{ "Name": "replacement", "Repository": "org/new", "Workflow": "c.yml", "Artifact": "c" }] }
            """));

        var result = CacheConfigurationLoader.Load(builder.Build());

        await Assert.That(result.Sources.Count).IsEqualTo(1);
        await Assert.That(result.Sources[0].Name).IsEqualTo("replacement");
        await Assert.That(result.Sources[0].Repository).IsEqualTo("org/new");
    }

    [Test]
    [NotInParallel]
    public async Task EnvironmentCanPopulateAnExplicitlyEmptySourcesArray()
    {
        Directory.CreateDirectory(_directory);
        var defaults = WriteJson("defaults.json", "{}");
        var empty = WriteJson("empty.json", """{ "Sources": [] }""");
        var variables = new Dictionary<string, string>
        {
            ["Sources__0__Name"] = "environment",
            ["Sources__0__Repository"] = "env/repo",
            ["Sources__0__Workflow"] = "env.yml",
            ["Sources__0__Artifact"] = "env-packages"
        };
        var saved = variables.Keys.ToDictionary(x => x, x => Environment.GetEnvironmentVariable(x));
        try
        {
            foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);
            var result = LoadFiles(defaults, Path.Combine(_directory, "missing.json"), empty);
            await Assert.That(result.Sources.Count).IsEqualTo(1);
            await Assert.That(result.Sources[0].Name).IsEqualTo("environment");
            await Assert.That(result.Sources[0].Repository).IsEqualTo("env/repo");
            await Assert.That(result.Sources[0].Workflow).IsEqualTo("env.yml");
            await Assert.That(result.Sources[0].Artifact).IsEqualTo("env-packages");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Test]
    public async Task LegacyAndModernServerFieldsFollowLayerPrecedence()
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonStream(Stream("""{ "Proxy": { "Repository": "org/old", "RefreshSeconds": 20 } }"""));
        builder.AddJsonStream(Stream("""
            { "Server": { "RefreshSeconds": 30 },
              "Sources": [{ "Name": "new", "Repository": "org/new", "Workflow": "ci.yml", "Artifact": "p" }] }
            """));
        builder.AddJsonStream(Stream("""{ "Proxy": { "RefreshSeconds": 40 } }"""));

        var result = CacheConfigurationLoader.Load(builder.Build());

        await Assert.That(result.Server.RefreshSeconds).IsEqualTo(40);
        await Assert.That(result.Sources.Single().Repository).IsEqualTo("org/new");
    }

    [Test]
    public async Task ANewerLegacySourceReplacesAnOlderModernSource()
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonStream(Stream("""
            { "Sources": [{ "Name": "new", "Repository": "org/new", "Workflow": "ci.yml", "Artifact": "p" }] }
            """));
        builder.AddJsonStream(Stream("""
            { "Proxy": { "Repository": "legacy/repo", "Workflow": "old.yml", "PackagesArtifact": "old" } }
            """));

        var result = CacheConfigurationLoader.Load(builder.Build());

        await Assert.That(result.Sources.Count).IsEqualTo(1);
        await Assert.That(result.Sources[0].Name).IsEqualTo("repo");
        await Assert.That(result.Sources[0].Repository).IsEqualTo("legacy/repo");
        await Assert.That(result.Sources[0].Workflow).IsEqualTo("old.yml");
        await Assert.That(result.Sources[0].Artifact).IsEqualTo("old");
    }

    [Test]
    [NotInParallel]
    public async Task LegacyEnvironmentOverridesLegacyFileWithoutReenablingRunSelection()
    {
        Directory.CreateDirectory(_directory);
        var defaults = WriteJson("defaults.json", "{}");
        var user = WriteJson("user.json", """
            { "Proxy": { "Repository": "org/old", "Workflow": "ci.yml", "PackagesArtifact": "p" } }
            """);
        var variables = new Dictionary<string, string>
        {
            ["Proxy__Repository"] = "env/new",
            ["Proxy__RunIds__0"] = "123"
        };
        var saved = variables.Keys.ToDictionary(x => x, x => Environment.GetEnvironmentVariable(x));
        try
        {
            foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);
            var result = LoadFiles(defaults, user, null);
            await Assert.That(result.Sources.Single().Name).IsEqualTo("new");
            await Assert.That(result.Sources.Single().Repository).IsEqualTo("env/new");
            await Assert.That(result.Sources.Single().Workflow).IsEqualTo("ci.yml");
            await Assert.That(ProxyOptions.FromConfiguration(result).RunIds).IsEmpty();
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Test]
    [NotInParallel]
    public async Task UserConfigPathUsesThePlatformSpecificConfigDirectory()
    {
        var name = OperatingSystem.IsWindows() ? "APPDATA" : "XDG_CONFIG_HOME";
        var saved = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, _directory);
            var expected = OperatingSystem.IsWindows()
                ? Path.Combine(_directory, "Keboo.127Cache", "config.json")
                : Path.Combine(_directory, "keboo-127cache", "config.json");
            await Assert.That(ConfigurationFiles.UserConfigPath()).IsEqualTo(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, saved);
        }
    }

    private CacheConfiguration LoadFiles(string defaults, string user, string? explicitFile)
    {
        var builder = new ConfigurationBuilder().AddJsonFile(defaults, optional: false);
        ConfigurationFiles.AddUserAndExplicitConfiguration(builder,
            explicitFile is null ? null : new FileInfo(explicitFile), user);
        return CacheConfigurationLoader.Load(builder.Build());
    }

    private string WriteJson(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static IConfiguration Json(string json) => new ConfigurationBuilder().AddJsonStream(Stream(json)).Build();
    private static Stream Stream(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static Dictionary<string, string?> ValidSource() => new()
    {
        ["Sources:0:Name"] = "source",
        ["Sources:0:Repository"] = "org/repo",
        ["Sources:0:Workflow"] = "build.yml",
        ["Sources:0:Artifact"] = "packages"
    };

    private static OptionsValidationException Invalid(IConfiguration config)
    {
        try { CacheConfigurationLoader.Load(config); }
        catch (OptionsValidationException error) { return error; }
        throw new InvalidOperationException("The configuration should have been rejected.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }
}
