using System.Net;
using System.Text.RegularExpressions;
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
        public required FixtureGitHub GitHub { get; init; }
        public PackageCatalog Catalog => App.Services.GetRequiredService<PackageCatalog>();

        public static async Task<Host> StartAsync(PackageRewriteFixture fixture)
        {
            var github = await fixture.CreateGitHubAsync();
            var cache = Path.Combine(fixture.DirectoryPath, "cache");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:CacheDirectory"] = cache,
                ["Sources:0:Name"] = "main", ["Sources:0:Repository"] = "org/main",
                ["Sources:0:Workflow"] = "build.yml", ["Sources:0:Artifact"] = "packages",
                ["Sources:0:VersionArtifact"] = "build-version"
            });
            builder.Logging.ClearProviders();
            builder.Services.AddProxy(builder.Configuration);
            builder.Services.AddSingleton<IBuildSourceFactory>(new SharedFactory(github));
            var app = builder.Build();
            app.UseExceptionHandler();
            app.MapProxy();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
            return new Host { App = app, Client = client, CacheDirectory = cache, GitHub = github };
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
        await Assert.That(html).Contains("Removed 1 cached build(s)");
        await Assert.That(html).Contains("downloaded again on the next refresh");
        await Assert.That(removed.RequestMessage!.RequestUri!.AbsolutePath).IsEqualTo("/");

        using var refreshed = await PostFormAsync(host, "", "refresh", Token(html), ("source", "main"));
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