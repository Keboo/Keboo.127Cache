using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keboo.LocalCache.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Keboo.LocalCache.Tests;

// Every test keeps its configuration files in the fixture's own temporary directory; nothing touches the real user config.
[NotInParallel]
public sealed class SourceManagementTests
{
    private sealed class SharedFactory(IGitHubClient github) : IBuildSourceFactory
    {
        public IBuildSource Create(ProxyOptions source) => new GitHubActionsBuildSource(source, github);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ConfigurationEnvironment _environment;
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string UserFile { get; init; }
        public required string? ExplicitFile { get; init; }
        public required string CacheDirectory { get; init; }
        public string WritableFile => ExplicitFile ?? UserFile;
        public PackageCatalog Catalog => App.Services.GetRequiredService<PackageCatalog>();
        public ISourceConfigurationService Service => App.Services.GetRequiredService<ISourceConfigurationService>();

        private Host(ConfigurationEnvironment environment) => _environment = environment;

        public static async Task<Host> StartAsync(PackageRewriteFixture fixture, string? userJson = null,
            string? explicitJson = null, Dictionary<string, string?>? environment = null)
        {
            var env = new ConfigurationEnvironment(environment ?? []);
            var github = await fixture.CreateGitHubAsync();
            var user = Path.Combine(fixture.DirectoryPath, "user", "config.json");
            var explicitFile = explicitJson is null ? null : Path.Combine(fixture.DirectoryPath, "explicit.json");
            var cache = Path.Combine(fixture.DirectoryPath, "cache");
            if (userJson is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(user)!);
                await File.WriteAllTextAsync(user, userJson);
            }
            if (explicitJson is not null) await File.WriteAllTextAsync(explicitFile!, explicitJson);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            ConfigurationFiles.AddUserAndExplicitConfiguration(builder.Configuration,
                explicitFile is null ? null : new FileInfo(explicitFile), user);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:CacheDirectory"] = cache });
            builder.Services.AddProxy(builder.Configuration, new SourceConfigurationLocations(user, explicitFile));
            builder.Services.AddSingleton<IBuildSourceFactory>(new SharedFactory(github));
            var app = builder.Build();
            app.UseExceptionHandler();
            app.MapProxy();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            return new Host(env)
            {
                App = app, Client = client, UserFile = user, ExplicitFile = explicitFile, CacheDirectory = cache
            };
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrfHeader = true,
            string? ifMatch = null, Action<HttpRequestMessage>? configure = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (csrfHeader) request.Headers.Add(ManagementRequestGuard.ClientHeader, "test");
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (body is not null) request.Content = JsonContent.Create(body);
            configure?.Invoke(request);
            return Client.SendAsync(request);
        }

        public string SourceDirectory(string name) =>
            Directory.CreateDirectory(CacheLayout.SourceDirectory(CacheDirectory, name)).FullName;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            _environment.Dispose();
        }
    }

    private static object Source(string name, string repository = "org/other", string workflow = "build.yml",
        string artifact = "packages", string? versionArtifact = null, string? type = null) =>
        new { name, repository, workflow, artifact, versionArtifact, type };

    private const string MainSource = """{ "Name": "main", "Repository": "org/main", "Workflow": "build.yml", "Artifact": "packages" }""";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static JsonObject ReadFile(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static string[] FileSourceNames(string path) =>
        ReadFile(path)["Sources"]!.AsArray().Select(x => x!["Name"]!.GetValue<string>()).ToArray();

    [Test]
    public async Task AddPersistsToTheExplicitFileKeepingOtherSettingsAndAppliesWithoutRestart()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson:
            $$"""{ "Server": { "RefreshSeconds": 300 }, "Custom": { "Keep": true }, "Sources": [ {{MainSource}} ] }""");

        using var response = await host.SendAsync(HttpMethod.Post, "api/sources",
            Source("extra", "org/extra", "ci.yml", "pkgs", "ver"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(response.Headers.ETag).IsNotNull();
        var body = await JsonAsync(response);
        await Assert.That(body.GetProperty("applied").GetProperty("added")[0].GetString()).IsEqualTo("extra");
        var file = ReadFile(host.ExplicitFile!);
        await Assert.That(file["Server"]!["RefreshSeconds"]!.GetValue<int>()).IsEqualTo(300);
        await Assert.That(file["Custom"]!["Keep"]!.GetValue<bool>()).IsTrue();
        await Assert.That(FileSourceNames(host.ExplicitFile!)).IsEquivalentTo(new[] { "main", "extra" });
        var extra = file["Sources"]!.AsArray()[1]!.AsObject();
        await Assert.That(extra["VersionArtifact"]!.GetValue<string>()).IsEqualTo("ver");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main", "extra" });
        await Assert.That(host.Catalog.Sources.Single(x => x.Name == "extra").Options.Repository).IsEqualTo("org/extra");
        await Assert.That(File.Exists(host.UserFile)).IsFalse();

        using var list = await host.SendAsync(HttpMethod.Get, "api/sources", csrfHeader: false);
        var sources = (await JsonAsync(list)).EnumerateArray().ToArray();
        await Assert.That(sources.Select(x => x.GetProperty("name").GetString()).ToArray()).IsEquivalentTo(new[] { "extra", "main" });
        await Assert.That(sources.All(x => x.GetProperty("editable").GetBoolean())).IsTrue();
    }

    [Test]
    public async Task WithoutAConfigFileTheUserConfigIsCreatedAndAFirstSourceIsServed()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        var before = await host.Service.GetConfigurationAsync();
        await Assert.That(before.WritableLayer).IsEqualTo("user");
        await Assert.That(before.FileExists).IsFalse();
        await Assert.That(before.IsEditable).IsTrue();
        await Assert.That(before.Sources.Count).IsEqualTo(0);

        var result = await host.Service.AddAsync(new SourceDefinition("first", "org/first", "build.yml", "packages"));

        await Assert.That(result.Status).IsEqualTo(SourceChangeStatus.Applied);
        await Assert.That(FileSourceNames(host.UserFile)).IsEquivalentTo(new[] { "first" });
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "first" });
        using var response = await CatalogTestSynchronization.GetWhenSettledAsync(host.Client, host.Catalog, "flat/acme.core/index.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var after = await host.Service.GetConfigurationAsync();
        await Assert.That(after.FileExists).IsTrue();
        await Assert.That(after.Sources.Select(x => x.Name).ToArray()).IsEquivalentTo(new[] { "first" });
    }

    [Test]
    public async Task InvalidDefinitionsReturnFieldErrorsAndNeverTouchTheFile()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""");
        var original = await File.ReadAllBytesAsync(host.ExplicitFile!);

        using var invalid = await host.SendAsync(HttpMethod.Post, "api/sources",
            Source("CON", "not-a-repo", "dir/build.yml", " ", type: "AzureDevOps"));
        using var duplicate = await host.SendAsync(HttpMethod.Post, "api/sources", Source("MAIN"));

        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(invalid.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        var errors = (await JsonAsync(invalid)).GetProperty("errors");
        foreach (var field in new[] { "name", "repository", "workflow", "artifact", "type" })
            await Assert.That(errors.TryGetProperty(field, out _)).IsTrue();
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await JsonAsync(duplicate)).GetProperty("errors").TryGetProperty("name", out _)).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(host.ExplicitFile!)).IsEquivalentTo(original);
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main" });
    }

    [Test]
    public async Task SourcesSetByEnvironmentVariablesShadowTheFileAndBlockEdits()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""",
            environment: new()
            {
                ["Sources__0__Name"] = "fromenv", ["Sources__0__Repository"] = "org/env",
                ["Sources__0__Workflow"] = "build.yml", ["Sources__0__Artifact"] = "packages"
            });
        var original = await File.ReadAllBytesAsync(host.ExplicitFile!);

        var info = await host.Service.GetConfigurationAsync();
        using var response = await host.SendAsync(HttpMethod.Post, "api/sources", Source("extra"));

        await Assert.That(info.IsEditable).IsFalse();
        await Assert.That(info.ShadowingLayers.Select(x => x.Kind).ToArray()).IsEquivalentTo(new[] { "environment" });
        await Assert.That(info.ReadOnlyReason).Contains("environment variables");
        await Assert.That(info.Sources.Select(x => x.Name).ToArray()).IsEquivalentTo(new[] { "fromenv" });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await File.ReadAllBytesAsync(host.ExplicitFile!)).IsEquivalentTo(original);
        using var list = await host.SendAsync(HttpMethod.Get, "api/sources", csrfHeader: false);
        await Assert.That((await JsonAsync(list)).EnumerateArray().Single().GetProperty("editable").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task ExplicitFileOverridingUserSourcesIsReportedButEditable()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, userJson: $$"""{ "Sources": [ {{MainSource}} ] }""",
            explicitJson: "{}");

        var info = await host.Service.GetConfigurationAsync();

        await Assert.That(info.IsEditable).IsTrue();
        await Assert.That(info.OverriddenLayers.Single().Path).IsEqualTo(Path.GetFullPath(host.UserFile));
        var result = await host.Service.AddAsync(new SourceDefinition("extra", "org/extra", "build.yml", "packages"));
        await Assert.That(result.Status).IsEqualTo(SourceChangeStatus.Applied);
        await Assert.That(FileSourceNames(host.ExplicitFile!)).IsEquivalentTo(new[] { "main", "extra" });
    }

    [Test]
    public async Task EditingRepositoryPurgesTheCacheRenameKeepsTheOldCacheAndUnchangedSourcesKeepTheirs()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""
            { "Sources": [ {{MainSource}},
              { "Name": "second", "Repository": "org/second", "Workflow": "build.yml", "Artifact": "packages" } ] }
            """);
        foreach (var name in new[] { "main", "second" })
            await File.WriteAllTextAsync(Path.Combine(host.SourceDirectory(name), "marker.txt"), name);

        using var edit = await host.SendAsync(HttpMethod.Put, "api/sources/second",
            Source("second", "org/second-fork"));
        await Assert.That(edit.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(File.Exists(Path.Combine(host.SourceDirectory("second"), "marker.txt"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(host.SourceDirectory("main"), "marker.txt"))).IsTrue();
        await Assert.That(ReadFile(host.ExplicitFile!)["Sources"]![1]!["Repository"]!.GetValue<string>()).IsEqualTo("org/second-fork");
        await Assert.That(host.Catalog.Sources.Single(x => x.Name == "second").Options.Repository).IsEqualTo("org/second-fork");

        using var rename = await host.SendAsync(HttpMethod.Put, "api/sources/main",
            Source("renamed", "org/main"));
        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "renamed", "second" });
        await Assert.That(File.Exists(Path.Combine(host.SourceDirectory("main"), "marker.txt"))).IsTrue();
    }

    [Test]
    public async Task RemoveKeepsTheCacheByDefaultAndDeletesItOnRequest()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""
            { "Sources": [ {{MainSource}},
              { "Name": "second", "Repository": "org/second", "Workflow": "build.yml", "Artifact": "packages" },
              { "Name": "third", "Repository": "org/third", "Workflow": "build.yml", "Artifact": "packages" } ] }
            """);
        foreach (var name in new[] { "second", "third" })
            await File.WriteAllTextAsync(Path.Combine(host.SourceDirectory(name), "marker.txt"), name);

        using var kept = await host.SendAsync(HttpMethod.Delete, "api/sources/second");
        using var removed = await host.SendAsync(HttpMethod.Delete, "api/sources/third?removeCache=true");
        using var missing = await host.SendAsync(HttpMethod.Delete, "api/sources/nope");

        await Assert.That(kept.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(FileSourceNames(host.ExplicitFile!)).IsEquivalentTo(new[] { "main" });
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main" });
        await Assert.That(File.Exists(Path.Combine(host.SourceDirectory("second"), "marker.txt"))).IsTrue();
        await Assert.That(Directory.Exists(CacheLayout.SourceDirectory(host.CacheDirectory, "third"))).IsFalse();
    }

    [Test]
    public async Task RemovingTheLastSourceLeavesAnEmptyFeed()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""");

        var result = await host.Service.RemoveAsync("main");

        await Assert.That(result.Status).IsEqualTo(SourceChangeStatus.Applied);
        await Assert.That(ReadFile(host.ExplicitFile!)["Sources"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(host.Catalog.Sources.Count).IsEqualTo(0);
        using var index = await host.SendAsync(HttpMethod.Get, "flat/acme.core/index.json", csrfHeader: false);
        await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ConcurrentAddsAreSerializedAndAllPersisted()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(index =>
            host.Service.AddAsync(new SourceDefinition("extra" + index, "org/extra" + index, "build.yml", "packages"))));

        await Assert.That(results.All(x => x.Succeeded)).IsTrue();
        var expected = new[] { "main" }.Concat(Enumerable.Range(0, 5).Select(x => "extra" + x)).ToArray();
        await Assert.That(FileSourceNames(host.ExplicitFile!)).IsEquivalentTo(expected);
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(expected);
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(host.ExplicitFile!)!, "*.tmp").Length).IsEqualTo(0);
    }

    [Test]
    public async Task ExternalEditsAreDetectedWithETagsAndCanBeReloaded()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""");
        var etag = (await host.Service.GetConfigurationAsync()).ETag!;
        await File.WriteAllTextAsync(host.ExplicitFile!, $$"""
            { "Sources": [ {{MainSource}},
              { "Name": "external", "Repository": "org/external", "Workflow": "build.yml", "Artifact": "packages" } ] }
            """);

        var changed = await host.Service.GetConfigurationAsync();
        using var stale = await host.SendAsync(HttpMethod.Post, "api/sources", Source("extra"), ifMatch: "\"" + etag + "\"");
        await Assert.That(changed.HasExternalChanges).IsTrue();
        await Assert.That(changed.ETag).IsNotEqualTo(etag);
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(FileSourceNames(host.ExplicitFile!)).IsEquivalentTo(new[] { "main", "external" });

        using var reload = await host.SendAsync(HttpMethod.Post, "api/sources/reload");
        await Assert.That(reload.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main", "external" });
        var current = await host.Service.GetConfigurationAsync();
        await Assert.That(current.HasExternalChanges).IsFalse();

        using var fresh = await host.SendAsync(HttpMethod.Post, "api/sources", Source("extra"), ifMatch: current.ETag);
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task AnInvalidConfigurationFileBlocksEditsWithoutBeingOverwritten()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""");
        await File.WriteAllTextAsync(host.ExplicitFile!, "{ not json");

        using var response = await host.SendAsync(HttpMethod.Post, "api/sources", Source("extra"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await File.ReadAllTextAsync(host.ExplicitFile!)).IsEqualTo("{ not json");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main" });
    }

    [Test]
    public async Task ChangesRequireTheClientHeaderAndSameOrigin()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: $$"""{ "Sources": [ {{MainSource}} ] }""");
        var original = await File.ReadAllBytesAsync(host.ExplicitFile!);

        using var noHeader = await host.SendAsync(HttpMethod.Post, "api/sources", Source("extra"), csrfHeader: false);
        using var crossOrigin = await host.SendAsync(HttpMethod.Put, "api/sources/main", Source("main"),
            configure: request => request.Headers.Add("Origin", "https://evil.example"));
        using var crossSite = await host.SendAsync(HttpMethod.Delete, "api/sources/main",
            configure: request => request.Headers.Add("Sec-Fetch-Site", "cross-site"));

        await Assert.That(noHeader.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(crossOrigin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(crossSite.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await File.ReadAllBytesAsync(host.ExplicitFile!)).IsEquivalentTo(original);
    }

    [Test]
    public async Task LegacyProxySourceFieldsAreMigratedIntoSources()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, explicitJson: """
            { "Proxy": { "Repository": "org/legacy", "Workflow": "build.yml", "PackagesArtifact": "packages", "RefreshSeconds": 99 } }
            """);

        var result = await host.Service.AddAsync(new SourceDefinition("extra", "org/extra", "build.yml", "packages"));

        await Assert.That(result.Status).IsEqualTo(SourceChangeStatus.Applied);
        var file = ReadFile(host.ExplicitFile!);
        await Assert.That(FileSourceNames(host.ExplicitFile!)).IsEquivalentTo(new[] { "legacy", "extra" });
        await Assert.That(file["Proxy"]!.AsObject().ContainsKey("Repository")).IsFalse();
        await Assert.That(file["Proxy"]!["RefreshSeconds"]!.GetValue<int>()).IsEqualTo(99);
    }

    [Test]
    public async Task ServiceWithoutAWritableLocationIsReadOnly()
    {
        using var fixture = new PackageRewriteFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = Path.Combine(fixture.DirectoryPath, "cache"),
            ["Sources:0:Name"] = "main", ["Sources:0:Repository"] = "org/main",
            ["Sources:0:Workflow"] = "build.yml", ["Sources:0:Artifact"] = "packages"
        });
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IBuildSourceFactory>(new SharedFactory(await fixture.CreateGitHubAsync()));
        await using var app = builder.Build();
        await app.StartAsync();

        var service = app.Services.GetRequiredService<ISourceConfigurationService>();
        var result = await service.AddAsync(new SourceDefinition("extra", "org/extra", "build.yml", "packages"));

        await Assert.That((await service.GetConfigurationAsync()).IsEditable).IsFalse();
        await Assert.That(result.Status).IsEqualTo(SourceChangeStatus.Blocked);
    }
}
