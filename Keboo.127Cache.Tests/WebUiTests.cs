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
            return new Host { App = app, Client = client, CacheDirectory = cache };
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
        await Assert.That(html).Contains(".pr.755.run.100.");
        await Assert.That(html).Contains("1.2.0-beta.3.pr.755.run.100.");
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
        await Assert.That(html).Contains("1.2.0-beta.3.pr.755.run.100.");
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
}
