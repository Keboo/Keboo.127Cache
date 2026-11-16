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

    private sealed class GatedGitHub(IGitHubClient inner) : IGitHubClient
    {
        public TaskCompletionSource DiscoveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DiscoveryRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken token) =>
            inner.GetOpenPullRequestsAsync(token);
        public async Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken token)
        {
            DiscoveryEntered.TrySetResult();
            await DiscoveryRelease.Task.WaitAsync(token);
            return await inner.GetRunsAsync(token);
        }
        public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long run, CancellationToken token) =>
            inner.GetArtifactsAsync(run, token);
        public async Task DownloadAsync(Artifact artifact, string destination, CancellationToken token)
        {
            DownloadEntered.TrySetResult();
            await DownloadRelease.Task.WaitAsync(token);
            await inner.DownloadAsync(artifact, destination, token);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string CacheDirectory { get; init; }
        public required string ConfigFile { get; init; }
        public required FixtureGitHub GitHub { get; init; }
        public PackageCatalog Catalog => App.Services.GetRequiredService<PackageCatalog>();

        public static async Task<Host> StartAsync(PackageRewriteFixture fixture, bool shadowed = false,
            Action<IServiceCollection>? configure = null, Func<IGitHubClient, IGitHubClient>? wrap = null)
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
                    ["Sources:0:Name"] = "main",
                    ["Sources:0:Repository"] = "org/shadow",
                    ["Sources:0:Workflow"] = "build.yml",
                    ["Sources:0:Artifact"] = "packages",
                    ["Sources:0:VersionArtifact"] = "build-version"
                });
            builder.Services.AddProxy(builder.Configuration,
                new SourceConfigurationLocations(Path.Combine(fixture.DirectoryPath, "user-config.json"), configFile));
            builder.Services.AddSingleton<IBuildSourceFactory>(new SharedFactory(wrap?.Invoke(github) ?? github));
            configure?.Invoke(builder.Services);
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

        public async Task<string> OverviewAsync()
        {
            await Client.GetStringAsync("");
            return await SettledOverviewAsync();
        }

        public async Task<string> SettledOverviewAsync()
        {
            await App.Services.GetRequiredService<OverviewLoader>().Completion.WaitAsync(TimeSpan.FromSeconds(15));
            return await Client.GetStringAsync("_ui/overview");
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

        await host.OverviewAsync();
        using var response = await host.Client.GetAsync("_ui/overview");
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        var feedUrl = host.App.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.BaseUrl.TrimEnd('/') + "/v3/index.json";
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
        var options = Options.Create(new ProxyOptions { BaseUrl = "http://localhost:55631/" });
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

    private static SourceInfo OverviewSource => new("main", "org/main", "build.yml", "packages", null,
        "ok", null, null, "current");

    private static BuildInfo OverviewBuild(long run, string state = "notCached") =>
        new("main", 755, run, 1, null, null, null, 0, state, true, null);

    private static Mock<IManagementService> OverviewManagement()
    {
        var management = new Mock<IManagementService>(MockBehavior.Strict);
        management.Setup(x => x.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([OverviewSource]);
        management.Setup(x => x.GetCachedPackagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return management;
    }

    [Test]
    public async Task RealOverviewWorkOutlivesFeedRequestBudgetsAndOnlyExplicitRefreshDownloadsArtifacts()
    {
        using var fixture = new PackageRewriteFixture();
        GatedGitHub upstream = null!;
        await using var host = await Host.StartAsync(fixture, wrap: inner => upstream = new GatedGitHub(inner));
        var management = host.App.Services.GetRequiredService<IManagementService>();
        var loader = host.App.Services.GetRequiredService<OverviewLoader>();
        await upstream.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var initial = await host.Client.GetStringAsync("").WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(initial).Contains("data-busy=\"true\"");
        await Assert.That(upstream.DiscoveryRelease.Task.IsCompleted).IsFalse();

        // The script/API read uses its existing one-second feed budget; the overview must still be following discovery.
        await management.GetBuildsAsync();
        await Assert.That(loader.Completion.IsCompleted).IsFalse();
        upstream.DiscoveryRelease.SetResult();
        var discovered = await host.SettledOverviewAsync();
        await Assert.That(discovered).Contains("Run 100");
        await Assert.That(discovered).Contains("Metadata only");
        await Assert.That(discovered).Contains("unknown");
        await Assert.That(host.GitHub.Downloads).IsEqualTo(0);
        await Assert.That(upstream.DownloadEntered.Task.IsCompleted).IsFalse();

        using var refresh = await PostFormAsync(host, "", "refresh", Token(discovered), ("source", "main"))
            .WaitAsync(TimeSpan.FromSeconds(15));
        refresh.EnsureSuccessStatusCode();
        await upstream.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var preparing = await host.Client.GetStringAsync("_ui/overview");
        await Assert.That(preparing).Contains("Preparing packages");
        try { await management.GetPackagesAsync(); }
        catch (DiscoveryUnavailableException) { }
        await Assert.That(loader.Completion.IsCompleted).IsFalse();
        await Assert.That(upstream.DownloadRelease.Task.IsCompleted).IsFalse();

        upstream.DownloadRelease.SetResult();
        var ready = await host.SettledOverviewAsync();
        await Assert.That(ready).Contains("Downloaded");
        await Assert.That(ready).Contains("<li>acme.core</li>", StringComparison.OrdinalIgnoreCase);
        await Assert.That(ready).Contains("1.2.0-beta.3.pr.755.run.");
        await Assert.That(ready).Contains("Refresh finished.");
        await Assert.That(host.GitHub.Downloads).IsEqualTo(2);
        await host.OverviewAsync();
        await Assert.That(host.GitHub.Downloads).IsEqualTo(2);
    }

    [Test]
    public async Task OverviewCompletesItsResponseBeforeDiscoveryAndDiskWorkAndPollsProgressWithoutDownloading()
    {
        using var fixture = new PackageRewriteFixture();
        var discovery = new TaskCompletionSource<IReadOnlyList<BuildInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventoryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disk = new TaskCompletionSource<CacheDiskUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var management = OverviewManagement();
        management.Setup(x => x.GetOverviewBuildsAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            discoveryEntered.TrySetResult();
            return discovery.Task.WaitAsync(token);
        });
        management.Setup(x => x.GetCachedPackagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            inventoryRead.TrySetResult();
            return [];
        });
        var eviction = new Mock<ICacheEvictionService>(MockBehavior.Strict);
        eviction.Setup(x => x.GetDiskUsageAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => disk.Task.WaitAsync(token));
        await using var host = await Host.StartAsync(fixture, configure: services =>
        {
            services.AddSingleton(management.Object);
            services.AddSingleton(eviction.Object);
        });

        // GetStringAsync buffers the ENTIRE response, not just headers or the first streamed chunk.
        var initial = await host.Client.GetStringAsync("").WaitAsync(TimeSpan.FromSeconds(15));
        await discoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(discovery.Task.IsCompleted).IsFalse();
        await Assert.That(disk.Task.IsCompleted).IsFalse();
        await Assert.That(initial).Contains("id=\"feed-url\"");
        await Assert.That(initial).Contains("href=\"/packages\"");
        await Assert.That(initial).Contains("data-busy=\"true\"");
        await Assert.That(initial).Contains("aria-busy=\"true\"");
        await Assert.That(initial).DoesNotContain("No builds.");

        var pending = await host.Client.GetStringAsync("_ui/overview");
        await Assert.That(pending).Contains("<th scope=\"row\">main</th>");
        await Assert.That(pending).Contains("Scanning");
        await Assert.That(pending).Contains("Discovering completed builds");
        await host.Client.GetStringAsync(""); // Another client/navigation joins, rather than starting duplicate work.
        management.Verify(x => x.GetOverviewBuildsAsync(It.IsAny<CancellationToken>()), Times.Once);

        discovery.SetResult([OverviewBuild(101)]);
        await inventoryRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var partial = await host.Client.GetStringAsync("_ui/overview");
        await Assert.That(partial).Contains("Run 101");
        await Assert.That(partial).Contains("Metadata only");
        await Assert.That(partial).Contains("data-busy=\"true\"");
        await Assert.That(partial).Contains("Scanning");

        disk.SetResult(new CacheDiskUsage(123, []));
        var complete = await host.SettledOverviewAsync();
        await Assert.That(complete).Contains("Overview ready.");
        await Assert.That(complete).Contains("data-busy=\"false\"");
        await Assert.That(complete).Contains("123 B");
        management.Verify(x => x.GetPackagesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task OverviewDisplaysBackgroundFailureAndRetryCanFinishWithAnAuthoritativeEmptyResult()
    {
        using var fixture = new PackageRewriteFixture();
        var discovery = new TaskCompletionSource<IReadOnlyList<BuildInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var management = OverviewManagement();
        management.SetupSequence(x => x.GetOverviewBuildsAsync(It.IsAny<CancellationToken>()))
            .Returns(discovery.Task).ReturnsAsync([]).ReturnsAsync([]);
        management.Setup(x => x.RefreshAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshSummary(true, []));
        management.Setup(x => x.PrepareOverviewPackagesAsync(null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await using var host = await Host.StartAsync(fixture, configure: services => services.AddSingleton(management.Object));

        var loading = await host.Client.GetStringAsync("").WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(loading).Contains("data-busy=\"true\"");
        discovery.SetException(new IOException("discovery is offline"));
        var failed = await host.SettledOverviewAsync();
        await Assert.That(failed).Contains("role=\"alert\"");
        await Assert.That(failed).Contains("discovery is offline");
        await Assert.That(failed).Contains("Builds unavailable.");
        await Assert.That(failed).DoesNotContain("No builds.");
        using var retry = await PostFormAsync(host, "", "refresh", Token(failed), ("source", ""));
        retry.EnsureSuccessStatusCode();
        var empty = await host.SettledOverviewAsync();
        await Assert.That(empty).Contains("Refresh finished.");
        await Assert.That(empty).Contains("No builds.");
        await Assert.That(empty).DoesNotContain("discovery is offline");
    }

    [Test]
    public async Task RefreshReturnsBeforeDiscoveryAndPreparationRetainsResultsAndCoalescesAcrossClients()
    {
        using var fixture = new PackageRewriteFixture();
        var discovery = new TaskCompletionSource<RefreshSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new TaskCompletionSource<IReadOnlyList<PackageInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var management = OverviewManagement();
        management.SetupSequence(x => x.GetOverviewBuildsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([OverviewBuild(100, "cached")])
            .ReturnsAsync([OverviewBuild(100, "cached"), OverviewBuild(101)])
            .ReturnsAsync([OverviewBuild(100, "cached"), OverviewBuild(101, "cached")]);
        management.Setup(x => x.RefreshAsync(null, It.IsAny<CancellationToken>())).Returns((string? _, CancellationToken token) =>
        {
            discoveryEntered.TrySetResult();
            return discovery.Task.WaitAsync(token);
        });
        management.Setup(x => x.PrepareOverviewPackagesAsync(null, It.IsAny<CancellationToken>())).Returns((string? _, CancellationToken token) =>
        {
            preparationEntered.TrySetResult();
            return preparation.Task.WaitAsync(token);
        });
        await using var host = await Host.StartAsync(fixture, configure: services => services.AddSingleton(management.Object));
        var before = await host.OverviewAsync();
        await Assert.That(before).Contains("Run 100");
        await Assert.That(before).Contains("Downloaded");

        using var refresh = await PostFormAsync(host, "", "refresh", Token(before), ("source", ""))
            .WaitAsync(TimeSpan.FromSeconds(15));
        refresh.EnsureSuccessStatusCode();
        var refreshing = await refresh.Content.ReadAsStringAsync();
        await discoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(discovery.Task.IsCompleted).IsFalse();
        await Assert.That(refreshing).Contains("Run 100");
        await Assert.That(refreshing).Contains("Downloaded");
        await Assert.That(refreshing).Contains("Discovering completed builds");
        await Assert.That(refreshing).Contains("disabled");

        using var duplicate = await PostFormAsync(host, "", "refresh", Token(refreshing), ("source", ""));
        duplicate.EnsureSuccessStatusCode();
        await host.Client.GetStringAsync("");
        management.Verify(x => x.RefreshAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        discovery.SetResult(new RefreshSummary(true, [new SourceRefreshResult("main", true, 2, null, null, false)]));
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var preparing = await host.Client.GetStringAsync("_ui/overview");
        await Assert.That(preparation.Task.IsCompleted).IsFalse();
        await Assert.That(preparing).Contains("Preparing packages");
        await Assert.That(preparing).Contains("Run 100");
        await Assert.That(preparing).Contains("Run 101");
        await Assert.That(preparing).Contains("Metadata only");
        await Assert.That(preparing).Contains("data-busy=\"true\"");
        preparation.SetResult([]);
        var finished = await host.SettledOverviewAsync();
        await Assert.That(finished).Contains("Refresh finished.");
        await Assert.That(finished).Contains("2 build(s) found");
        await Assert.That(finished).Contains("Run 101");
        await Assert.That(finished).DoesNotContain("Metadata only");
        management.Verify(x => x.PrepareOverviewPackagesAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        management.Verify(x => x.GetOverviewBuildsAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public async Task HostShutdownCancelsAndDrainsOverviewWork()
    {
        using var fixture = new PackageRewriteFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var management = OverviewManagement();
        management.Setup(x => x.GetOverviewBuildsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.SetResult();
                    throw;
                }
                return Array.Empty<BuildInfo>();
            });
        await using var host = await Host.StartAsync(fixture, configure: services => services.AddSingleton(management.Object));
        await host.Client.GetStringAsync("");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await host.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(host.App.Services.GetRequiredService<OverviewLoader>().Completion.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task RefreshButtonsPostWithAntiforgeryAndShowTheResultPerSource()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        var page = await host.OverviewAsync();

        await Assert.That(page).Contains("Refresh all sources");
        await Assert.That(page).Contains("name=\"source\" value=\"main\"");
        await Assert.That(page).Contains("form=\"refresh-form\"");
        await Assert.That(page).Contains("method=\"post\"");
        var token = Token(page);

        using var one = await PostFormAsync(host, "", "refresh", token, ("source", "main"));
        var oneHtml = await host.SettledOverviewAsync();
        await Assert.That(one.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(oneHtml).Contains("Refresh finished.");
        await Assert.That(oneHtml).Contains("<strong>main</strong>");
        await Assert.That(oneHtml).Contains("1 build(s) found");
        await Assert.That(oneHtml).Contains("Downloaded");

        using var all = await PostFormAsync(host, "", "refresh", token, ("source", ""));
        await Assert.That(await host.SettledOverviewAsync()).Contains("Refresh finished.");

        using var forged = await host.Client.PostAsync("", new FormUrlEncodedContent([new("_handler", "refresh"), new("source", "main")]));
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RefreshErrorsAreDisplayed()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        var token = Token(await host.OverviewAsync());

        using var response = await PostFormAsync(host, "", "refresh", token, ("source", "nope"));
        var html = await host.SettledOverviewAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("role=\"alert\"");
        await Assert.That(html).Contains("Could not update the overview:");
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
        await Assert.That(editPage).Contains("clears this source's cache");
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
        await Assert.That(removePage).Contains("Also permanently delete this source's cached builds");
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
        await Assert.That(invalidHtml).Contains("aria-invalid");
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
            ("Workflow", "build.yml"), ("Artifact", "packages"), ("VersionArtifact", ""), ("ExpectedETag", staleTag));
        var conflictHtml = await stale.Content.ReadAsStringAsync();
        if (stale.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Source form returned {(int)stale.StatusCode}: {conflictHtml}");
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
        var overview = await host.OverviewAsync();
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

        using var refreshed = await PostFormAsync(host, "", "refresh", Token(await host.OverviewAsync()), ("source", "main"));
        var after = await host.SettledOverviewAsync();
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