using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Keboo.LocalCache.Testing;
using Keboo.LocalCache.Ui;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache.Tests;

public sealed class WebUiTests
{
    private sealed class SharedFactory(IGitHubClient github) : IBuildSourceFactory
    {
        public IBuildSource Create(ProxyOptions source) => new GitHubActionsBuildSource(source, github);
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string CacheDirectory { get; init; }
        public required string ConfigFile { get; init; }
        public required FixtureGitHub GitHub { get; init; }
        public PackageCatalog Catalog => App.Services.GetRequiredService<PackageCatalog>();

        public static async Task<Host> StartAsync(PackageRewriteFixture fixture, bool shadowed = false)
        {
            var github = await fixture.CreateGitHubAsync();
            var cache = Path.Combine(fixture.DirectoryPath, "cache");
            var configFile = Path.Combine(fixture.DirectoryPath, "ui-config.json");
            await File.WriteAllTextAsync(configFile, """
                {
                  "Sources": [
                    {
                      "Name": "main",
                      "Type": "GitHubActions",
                      "Repository": "org/main",
                      "Workflow": "build.yml",
                      "Artifact": "packages",
                      "VersionArtifact": "build-version"
                    }
                  ]
                }
                """);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            ConfigurationFiles.AddUserAndExplicitConfiguration(builder.Configuration,
                new FileInfo(configFile), Path.Combine(fixture.DirectoryPath, "user-config.json"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:CacheDirectory"] = cache
            });
            builder.Logging.ClearProviders();
            if (shadowed)
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sources:0:Name"] = "main", ["Sources:0:Repository"] = "org/shadow",
                    ["Sources:0:Workflow"] = "build.yml", ["Sources:0:Artifact"] = "packages",
                    ["Sources:0:VersionArtifact"] = "build-version"
                });
            builder.Services.AddProxy(builder.Configuration,
                new SourceConfigurationLocations(Path.Combine(fixture.DirectoryPath, "user-config.json"), configFile));
            builder.Services.AddSingleton<IBuildSourceFactory>(new SharedFactory(github));
            var app = builder.Build();
            app.UseExceptionHandler();
            app.MapProxy();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            return new Host { App = app, Client = client, CacheDirectory = cache, ConfigFile = configFile, GitHub = github };
        }

        public async Task PopulateAsync()
        {
            using var response = await CatalogTestSynchronization.GetWhenSettledAsync(Client, Catalog, "flat/acme.core/index.json");
            response.EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class RecordingLauncher : IBrowserLauncher
    {
        public List<string> Opened { get; } = [];
        public Exception? Failure { get; init; }
        public void Open(string url)
        {
            if (Failure is not null) throw Failure;
            Opened.Add(url);
        }
    }

    [Test]
    public async Task OverviewShowsFeedUrlSourcesAndBuildsGroupedBySourcePullRequestRun()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var response = await host.Client.GetAsync("");
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        var feedUrl = host.App.Services.GetRequiredService<ProxyOptions>().BaseUrl.TrimEnd('/') + "/v3/index.json";
        await Assert.That(html).Contains($"value=\"{feedUrl}\"");
        await Assert.That(html).Contains("readonly");
        // Source status: name, repository, workflow, artifact, status and last refresh.
        await Assert.That(html).Contains("<th scope=\"row\">main</th>");
        await Assert.That(html).Contains("href=\"https://github.com/org/main\"");
        await Assert.That(html).Contains("build.yml");
        await Assert.That(html).Contains("status-ok");
        // Builds table: caption, rowgroup per pull request, run link, package ids, rewritten and original version.
        await Assert.That(html).Contains("<caption>Builds of source main</caption>");
        await Assert.That(html).Contains("scope=\"rowgroup\"");
        await Assert.That(html).Contains("href=\"https://github.com/org/main/pull/755\"");
        await Assert.That(html).Contains("href=\"https://github.com/org/main/actions/runs/100/attempts/1\"");
        await Assert.That(html).Contains("<li>acme.core</li>", StringComparison.OrdinalIgnoreCase);
        await Assert.That(html).Contains(".pr.755.run.r0000000000000000100.attempt.1.");
        await Assert.That(html).Contains("1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.");
        await Assert.That(html).Contains("Downloaded");
        await Assert.That(html).Contains("scope=\"col\"");
    }

    [Test]
    public async Task PagesLoadNothingFromOtherOriginsAndShipNoFrameworkScript()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        var html = await host.Client.GetStringAsync("");
        var references = Regex.Matches(html, "<(?:script|link|img)[^>]+(?:src|href)=\"([^\"]+)\"")
            .Select(x => x.Groups[1].Value).ToArray();

        await Assert.That(references).IsEquivalentTo(["/_ui/app.css", "/_ui/app.js"]);
        await Assert.That(html).DoesNotContain("blazor");
        using var css = await host.Client.GetAsync("_ui/app.css");
        using var script = await host.Client.GetAsync("_ui/app.js");
        await Assert.That(css.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(css.Content.Headers.ContentType?.MediaType).IsEqualTo("text/css");
        await Assert.That(script.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(script.Content.Headers.ContentType?.MediaType).IsEqualTo("text/javascript");
    }

    [Test]
    public async Task PackagesPageListsVersionsPerPackage()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        var html = await host.Client.GetStringAsync("packages");

        await Assert.That(html).Contains("<caption>acme.core</caption>", StringComparison.OrdinalIgnoreCase);
        await Assert.That(html).Contains("1.2.0-beta.3.pr.755.run.r0000000000000000100.attempt.1.");
        await Assert.That(html).Contains("#755");
    }

    [Test]
    public async Task ExistingEndpointsKeepTheirRoutes()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        foreach (var path in new[] { "health", "builds", "api/sources", "v3/index.json", "flat/acme.core/index.json" })
        {
            using var response = await host.Client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).Contains("json");
        }
        using var unknown = await host.Client.GetAsync("no-such-page");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UiRequestsFromForeignHostsOrOriginsAreRejected()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "");
        rebinding.Headers.Host = "evil.example";
        using var rebound = await host.Client.SendAsync(rebinding);
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "") { Content = new FormUrlEncodedContent([]) };
        crossSite.Headers.Add("Origin", "http://evil.example");
        using var cross = await host.Client.SendAsync(crossSite);

        await Assert.That(rebound.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(cross.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AntiforgeryKeyRingIsStoredInsideTheCacheDirectory()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);

        var repository = host.App.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
        var context = new DefaultHttpContext { RequestServices = host.App.Services };
        var tokens = host.App.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);

        var ring = Path.Combine(host.CacheDirectory, WebUiExtensions.KeyRingDirectoryName);
        await Assert.That(repository).IsTypeOf<FileSystemXmlRepository>();
        await Assert.That(((FileSystemXmlRepository)repository!).Directory.FullName).IsEqualTo(Path.GetFullPath(ring));
        await Assert.That(tokens.RequestToken).IsNotNull();
        await Assert.That(Directory.EnumerateFiles(ring, "key-*.xml")).IsNotEmpty();
    }

    [Test]
    public async Task StartupLogsTheUiUrlAndOpensTheBrowserOnlyWhenRequested()
    {
        var options = new ProxyOptions { BaseUrl = "http://localhost:55631/" };
        var lifetime = new Mock<IHostApplicationLifetime>().Object;
        var quiet = new RecordingLauncher();
        var quietLog = new RecordingLogger<WebUiStartup>();
        new WebUiStartup(options, lifetime, quiet, quietLog, new WebUiStartupOptions(false)).OnStarted();
        var open = new RecordingLauncher();
        var openLog = new RecordingLogger<WebUiStartup>();
        new WebUiStartup(options, lifetime, open, openLog, new WebUiStartupOptions(true)).OnStarted();
        var broken = new RecordingLogger<WebUiStartup>();
        new WebUiStartup(options, lifetime, new RecordingLauncher { Failure = new InvalidOperationException("no browser") },
            broken, new WebUiStartupOptions(true)).OnStarted();

        await Assert.That(quietLog.Messages).Contains("Web UI available at http://localhost:55631/");
        await Assert.That(quiet.Opened).IsEmpty();
        await Assert.That(open.Opened).IsEquivalentTo(["http://localhost:55631/"]);
        await Assert.That(broken.Messages.Last()).Contains("no browser");
    }

    [Test]
    public async Task ServeAcceptsOpenFlag()
    {
        var result = Program.BuildCommandLine().Parse("serve --open");

        await Assert.That(result.Errors).IsEmpty();
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "__RequestVerificationToken[^>]*value=\"([^\"]+)\"");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : throw new InvalidOperationException("No antiforgery token in the page.");
    }

    private static string HiddenValue(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : throw new InvalidOperationException($"No {name} field in the page.");
    }

    private static Task<HttpResponseMessage> PostFormAsync(Host host, string url, string handler, string token, params (string, string)[] fields) =>
        host.Client.PostAsync(url, new FormUrlEncodedContent(new[] { ("_handler", handler), ("__RequestVerificationToken", token) }
            .Concat(fields).Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2))));

    [Test]
    public async Task RefreshButtonsPostWithAntiforgeryAndShowTheResultPerSource()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        var page = await host.Client.GetStringAsync("");

        await Assert.That(page).Contains("Refresh all sources");
        await Assert.That(page).Contains("name=\"source\" value=\"main\"");
        await Assert.That(page).Contains("form=\"refresh-form\"");
        await Assert.That(page).Contains("method=\"post\"");
        var token = Token(page);

        using var one = await PostFormAsync(host, "", "refresh", token, ("source", "main"));
        var oneHtml = await one.Content.ReadAsStringAsync();
        await Assert.That(one.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(oneHtml).Contains("Refresh finished.");
        await Assert.That(oneHtml).Contains("<strong>main</strong>");
        await Assert.That(oneHtml).Contains("1 build(s) found");
        await Assert.That(oneHtml).Contains("Downloaded");

        using var all = await PostFormAsync(host, "", "refresh", token, ("source", ""));
        await Assert.That(await all.Content.ReadAsStringAsync()).Contains("Refresh finished.");

        using var forged = await host.Client.PostAsync("", new FormUrlEncodedContent([new("_handler", "refresh"), new("source", "main")]));
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RefreshErrorsAreDisplayed()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        var token = Token(await host.Client.GetStringAsync(""));

        using var response = await PostFormAsync(host, "", "refresh", token, ("source", "nope"));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("role=\"alert\"");
        await Assert.That(html).Contains("Refresh failed:");
        await Assert.That(html).Contains("Source &#x27;nope&#x27; is not configured");
    }

    [Test]
    public async Task SourcesPageAddsEditsAndRemovesPersistedSourcesAndHotAppliesChanges()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);

        var page = await host.Client.GetStringAsync("sources");
        await Assert.That(page).Contains("Manage sources");
        await Assert.That(page).Contains("ui-config.json");
        await Assert.That(page).Contains("main");
        await Assert.That(page).Contains("Version artifact (optional)");
        await Assert.That(page).Contains("Add source");

        using var add = await PostFormAsync(host, "sources", "add", Token(page),
            ("Name", "extra"), ("Type", "GitHubActions"), ("Repository", "org/extra"),
            ("Workflow", "ci.yml"), ("Artifact", "pkgs"), ("VersionArtifact", "extra-version"),
            ("ExpectedETag", HiddenValue(page, "ExpectedETag")));
        var addedHtml = await add.Content.ReadAsStringAsync();
        await Assert.That(add.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(addedHtml).Contains("Source added and applied to the running feed.");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main", "extra" });
        var addedConfig = JsonNode.Parse(await File.ReadAllTextAsync(host.ConfigFile))!.AsObject();
        var extra = addedConfig["Sources"]!.AsArray().Single(x => x!["Name"]!.GetValue<string>() == "extra");
        await Assert.That(extra!["VersionArtifact"]!.GetValue<string>()).IsEqualTo("extra-version");

        var editPage = await host.Client.GetStringAsync("sources?edit=main");
        await Assert.That(editPage).Contains("clears this source&#x27;s cache");
        var mainCache = CacheLayout.SourceDirectory(host.CacheDirectory, "main");
        Directory.CreateDirectory(mainCache);
        var cacheMarker = Path.Combine(mainCache, "marker.txt");
        await File.WriteAllTextAsync(cacheMarker, "cached");
        using var edit = await PostFormAsync(host, "sources?edit=main", "edit", Token(editPage),
            ("OriginalName", "main"), ("Name", "main"), ("Type", "GitHubActions"),
            ("Repository", "org/changed"), ("Workflow", "build.yml"), ("Artifact", "packages"),
            ("VersionArtifact", ""), ("ExpectedETag", HiddenValue(editPage, "ExpectedETag")));
        var editedHtml = await edit.Content.ReadAsStringAsync();
        await Assert.That(edit.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(editedHtml).Contains("Source saved and applied to the running feed.");
        await Assert.That(File.Exists(cacheMarker)).IsFalse();
        await Assert.That(host.Catalog.Sources.Single(x => x.Name == "main").Options.Repository).IsEqualTo("org/changed");
        var editedConfig = JsonNode.Parse(await File.ReadAllTextAsync(host.ConfigFile))!.AsObject();
        var main = editedConfig["Sources"]!.AsArray().Single(x => x!["Name"]!.GetValue<string>() == "main");
        await Assert.That(main!["VersionArtifact"]).IsNull();

        var extraCache = CacheLayout.SourceDirectory(host.CacheDirectory, "extra");
        Directory.CreateDirectory(extraCache);
        var oldCacheMarker = Path.Combine(extraCache, "marker.txt");
        await File.WriteAllTextAsync(oldCacheMarker, "old cached builds");
        var renamePage = await host.Client.GetStringAsync("sources?edit=extra");
        using var rename = await PostFormAsync(host, "sources?edit=extra", "edit", Token(renamePage),
            ("OriginalName", "extra"), ("Name", "renamed"), ("Type", "GitHubActions"),
            ("Repository", "org/extra"), ("Workflow", "ci.yml"), ("Artifact", "pkgs"),
            ("VersionArtifact", "extra-version"), ("ExpectedETag", HiddenValue(renamePage, "ExpectedETag")));
        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await rename.Content.ReadAsStringAsync())).Contains("Source saved and applied to the running feed.");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main", "renamed" });
        await Assert.That(File.Exists(oldCacheMarker)).IsTrue();

        var removePage = await host.Client.GetStringAsync("sources?remove=renamed");
        await Assert.That(removePage).Contains("Also permanently delete this source&#x27;s cached builds");
        var renamedCache = CacheLayout.SourceDirectory(host.CacheDirectory, "renamed");
        Directory.CreateDirectory(renamedCache);
        await File.WriteAllTextAsync(Path.Combine(renamedCache, "marker.txt"), "cached");
        using var remove = await PostFormAsync(host, "sources?remove=renamed", "remove", Token(removePage),
            ("RemoveName", "renamed"), ("ExpectedETag", HiddenValue(removePage, "ExpectedETag")), ("RemoveCache", "true"));
        var removedHtml = await remove.Content.ReadAsStringAsync();
        await Assert.That(remove.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(removedHtml).Contains("Source removed from the configuration and running feed.");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main" });
        await Assert.That(Directory.Exists(renamedCache)).IsFalse();
        await Assert.That(File.Exists(oldCacheMarker)).IsTrue();
        var removedConfig = JsonNode.Parse(await File.ReadAllTextAsync(host.ConfigFile))!.AsObject();
        await Assert.That(removedConfig["Sources"]!.AsArray().Select(x => x!["Name"]!.GetValue<string>()).ToArray())
            .IsEquivalentTo(new[] { "main" });
    }

    [Test]
    public async Task SourcesPageShowsFieldValidationAndRejectsMissingAntiforgeryToken()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        var page = await host.Client.GetStringAsync("sources");
        var fields = new[]
        {
            ("Name", "CON"), ("Type", "AzureDevOps"), ("Repository", "not-a-repo"),
            ("Workflow", "dir/build.yml"), ("Artifact", " "), ("VersionArtifact", ""),
            ("ExpectedETag", HiddenValue(page, "ExpectedETag"))
        };

        using var invalid = await PostFormAsync(host, "sources", "add", Token(page), fields);
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(invalidHtml).Contains("source-name-error");
        await Assert.That(invalidHtml).Contains("source-type-error");
        await Assert.That(invalidHtml).Contains("source-repository-error");
        await Assert.That(invalidHtml).Contains("source-workflow-error");
        await Assert.That(invalidHtml).Contains("source-artifact-error");
        await Assert.That(invalidHtml).Contains("aria-invalid=\"True\"");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main" });

        using var forged = await host.Client.PostAsync("sources", new FormUrlEncodedContent(
            [new("_handler", "add"), new("Name", "forged"), new("Type", "GitHubActions")]));
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SourcesPageWarnsWhenConfigurationIsShadowed()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture, shadowed: true);

        var html = await host.Client.GetStringAsync("sources");

        await Assert.That(html).Contains("higher-precedence configuration layer shadows this file");
        await Assert.That(html).Contains("MemoryConfigurationProvider");
        await Assert.That(html).Contains("org/shadow");
        await Assert.That(html).Contains("Read only");
        await Assert.That(html).DoesNotContain("href=\"/sources?edit=main\"");
    }

    [Test]
    public async Task SourcesPageRejectsStaleEtagsAndCanReloadExternalChanges()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        var page = await host.Client.GetStringAsync("sources");
        var staleTag = HiddenValue(page, "ExpectedETag");
        await File.WriteAllTextAsync(host.ConfigFile, """
            {
              "Sources": [
                { "Name": "main", "Repository": "org/main", "Workflow": "build.yml", "Artifact": "packages" },
                { "Name": "external", "Repository": "org/external", "Workflow": "build.yml", "Artifact": "packages" }
              ]
            }
            """);

        using var stale = await PostFormAsync(host, "sources", "add", Token(page),
            ("Name", "stale"), ("Type", "GitHubActions"), ("Repository", "org/stale"),
            ("Workflow", "build.yml"), ("Artifact", "packages"), ("ExpectedETag", staleTag));
        var conflictHtml = await stale.Content.ReadAsStringAsync();
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(conflictHtml).Contains("changed since it was read");
        await Assert.That(conflictHtml).Contains("Review the current configuration");
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(host.ConfigFile))!["Sources"]!.AsArray().Count)
            .IsEqualTo(2);

        var reloadPage = await host.Client.GetStringAsync("sources");
        await Assert.That(reloadPage).Contains("The configuration file changed outside this server.");
        using var reload = await PostFormAsync(host, "sources", "reload", Token(reloadPage));
        var reloadedHtml = await reload.Content.ReadAsStringAsync();
        await Assert.That(reload.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(reloadedHtml).Contains("Configuration reloaded and applied to the running feed.");
        await Assert.That(host.Catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "main", "external" });
    }

    [Test]
    public async Task RemovingABuildAsksForConfirmationThenRefreshDownloadsItAgain()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        const string Url = "remove?scope=build&source=main&run=100&attempt=1";
        var overview = await host.Client.GetStringAsync("");
        await Assert.That(overview).Contains("href=\"/remove?scope=build&amp;source=main&amp;run=100&amp;attempt=1\"");
        await Assert.That(overview).Contains("Remove closed-PR builds");
        var expected = UiFormat.Size((await host.App.Services.GetRequiredService<IManagementService>().GetBuildsAsync()).Single().SizeBytes);

        var confirm = await host.Client.GetStringAsync(Url);

        await Assert.That(confirm).Contains($"<strong>{expected}</strong>");
        await Assert.That(confirm).Contains("downloaded again by the");
        await Assert.That(confirm).Contains("cannot be restored");
        await Assert.That(confirm).Contains("pull request is open: downloaded again on the next refresh");
        var builds = await host.App.Services.GetRequiredService<IManagementService>().GetBuildsAsync();
        await Assert.That(builds.Single().CachedState).IsEqualTo("cached");

        var token = Token(confirm);
        using var removed = await PostFormAsync(host, Url, "remove", token,
            ("scope", "build"), ("source", "main"), ("run", "100"), ("attempt", "1"));
        var html = await removed.Content.ReadAsStringAsync();
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("Removed 1 build(s) and reclaimed");
        await Assert.That(html).Contains("removed (pull request is open: downloaded again on the next refresh)");
        var afterRemoval = await host.App.Services.GetRequiredService<IManagementService>().GetBuildsAsync();
        await Assert.That(afterRemoval.Single().CachedState).IsEqualTo("notCached");

        using var refreshed = await PostFormAsync(host, "", "refresh", Token(await host.Client.GetStringAsync("")), ("source", "main"));
        var after = await refreshed.Content.ReadAsStringAsync();
        await Assert.That(after).Contains("Refresh finished.");
        await Assert.That(after).Contains("Downloaded");
        await Assert.That(after).DoesNotContain("Metadata only");
        await Assert.That(host.GitHub.Downloads).IsGreaterThan(2);
    }

    [Test]
    public async Task RemovingClosedPullRequestBuildsAndRemovalErrorsAreDisplayed()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        var closed = await host.Client.GetStringAsync("remove?scope=closed");
        await Assert.That(closed).Contains("Nothing to remove");

        var unknown = await host.Client.GetStringAsync("remove?scope=build&source=nope&run=1&attempt=1");
        await Assert.That(unknown).Contains("role=\"alert\"");
        await Assert.That(unknown).Contains("nope");
        await Assert.That(unknown).DoesNotContain("name=\"__RequestVerificationToken\"");

        var invalid = await host.Client.GetStringAsync("remove?scope=build");
        await Assert.That(invalid).Contains("does not say which builds to remove");

        using var forged = await host.Client.PostAsync("remove", new FormUrlEncodedContent(
            [new("_handler", "remove"), new("scope", "closed")]));
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}