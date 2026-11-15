using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Keboo.LocalCache.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Keboo.LocalCache.Tests;

public sealed class ManagementApiTests
{
    private sealed class CountingGitHub(IGitHubClient inner) : IGitHubClient
    {
        private int _runListings;
        public int RunListings => Volatile.Read(ref _runListings);
        public TaskCompletionSource? Entered { get; set; }
        public TaskCompletionSource? Release { get; set; }

        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
            inner.GetOpenPullRequestsAsync(cancellationToken);

        public async Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runListings);
            Entered?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            return await inner.GetRunsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken) =>
            inner.GetArtifactsAsync(runId, cancellationToken);

        public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken) =>
            inner.DownloadAsync(artifact, destination, cancellationToken);
    }

    private sealed class SharedFactory(IGitHubClient github) : IBuildSourceFactory
    {
        public IBuildSource Create(ProxyOptions source) => new GitHubActionsBuildSource(source, github);
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required CountingGitHub GitHub { get; init; }
        public required HttpClient Client { get; init; }
        public PackageCatalog Catalog => App.Services.GetRequiredService<PackageCatalog>();

        public static async Task<Host> StartAsync(PackageRewriteFixture fixture)
        {
            var github = new CountingGitHub(await fixture.CreateGitHubAsync());
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:CacheDirectory"] = Path.Combine(fixture.DirectoryPath, "cache"),
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
            return new Host { App = app, GitHub = github, Client = client };
        }

        public async Task PopulateAsync()
        {
            using var response = await CatalogTestSynchronization.GetWhenSettledAsync(Client, Catalog, "flat/acme.core/index.json");
            response.EnsureSuccessStatusCode();
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, bool csrfHeader = true,
            Action<HttpRequestMessage>? configure = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (csrfHeader) request.Headers.Add(ManagementRequestGuard.ClientHeader, "test");
            configure?.Invoke(request);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        var body = await JsonAsync(response);
        await Assert.That(body.GetProperty("status").GetInt32()).IsEqualTo((int)status);
        await Assert.That(body.GetProperty("title").GetString()).IsNotNull();
    }

    [Test]
    public async Task SourcesListsConfigurationAndStatus()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "api/sources", csrfHeader: false);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues(ManagementRequestGuard.ApiVersionHeader).Single()).IsEqualTo("1");
        var source = (await JsonAsync(response)).EnumerateArray().Single();
        await Assert.That(source.GetProperty("name").GetString()).IsEqualTo("main");
        await Assert.That(source.GetProperty("repository").GetString()).IsEqualTo("org/main");
        await Assert.That(source.GetProperty("workflow").GetString()).IsEqualTo("build.yml");
        await Assert.That(source.GetProperty("artifact").GetString()).IsEqualTo("packages");
        await Assert.That(source.GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(source.GetProperty("lastRefreshUtc").ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(source.GetProperty("lastError").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task BuildsReportVersionSizeAndCachedState()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "api/builds?source=main", csrfHeader: false);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var build = (await JsonAsync(response)).EnumerateArray().Single();
        await Assert.That(build.GetProperty("source").GetString()).IsEqualTo("main");
        await Assert.That(build.GetProperty("pullRequest").GetInt32()).IsEqualTo(755);
        await Assert.That(build.GetProperty("runId").GetInt64()).IsEqualTo(100);
        await Assert.That(build.GetProperty("attempt").GetInt32()).IsEqualTo(1);
        await Assert.That(build.GetProperty("cachedState").GetString()).IsEqualTo("cached");
        await Assert.That(build.GetProperty("served").GetBoolean()).IsTrue();
        await Assert.That(build.GetProperty("sizeBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(build.GetProperty("version").GetString()).Contains(".pr.755.run.");

        using var unknown = await host.SendAsync(HttpMethod.Get, "api/builds?source=nope", csrfHeader: false);
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound);
    }

    [Test]
    public async Task PackagesGroupVersionsBySourceAndPullRequest()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "api/packages", csrfHeader: false);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var package = (await JsonAsync(response)).EnumerateArray()
            .First(x => string.Equals(x.GetProperty("id").GetString(), "acme.core", StringComparison.OrdinalIgnoreCase));
        var version = package.GetProperty("versions").EnumerateArray().Single();
        await Assert.That(version.GetProperty("source").GetString()).IsEqualTo("main");
        await Assert.That(version.GetProperty("pullRequest").GetInt32()).IsEqualTo(755);
        await Assert.That(version.GetProperty("version").GetString()).Contains(".pr.755.run.");

        using var other = await host.SendAsync(HttpMethod.Get, "api/packages?source=nope", csrfHeader: false);
        await AssertProblemAsync(other, HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RefreshBypassesTheMemoryCache()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        using var warm = await host.SendAsync(HttpMethod.Get, "api/builds", csrfHeader: false);
        var listings = host.GitHub.RunListings;

        using var response = await host.SendAsync(HttpMethod.Post, "api/refresh?source=main");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.GitHub.RunListings).IsEqualTo(listings + 1);
        var body = await JsonAsync(response);
        await Assert.That(body.GetProperty("succeeded").GetBoolean()).IsTrue();
        var result = body.GetProperty("sources").EnumerateArray().Single();
        await Assert.That(result.GetProperty("source").GetString()).IsEqualTo("main");
        await Assert.That(result.GetProperty("succeeded").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("builds").GetInt32()).IsEqualTo(1);

        using var all = await host.SendAsync(HttpMethod.Post, "api/refresh");
        await Assert.That(all.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.GitHub.RunListings).IsEqualTo(listings + 2);

        using var unknown = await host.SendAsync(HttpMethod.Post, "api/refresh?source=nope");
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ConcurrentRefreshesShareOneDiscovery()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        var management = host.App.Services.GetRequiredService<IManagementService>();
        var listings = host.GitHub.RunListings;
        host.GitHub.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.GitHub.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = management.RefreshAsync("main");
        await host.GitHub.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = management.RefreshAsync("main");
        host.GitHub.Release.SetResult();
        var results = await Task.WhenAll(first, second);

        await Assert.That(host.GitHub.RunListings).IsEqualTo(listings + 1);
        await Assert.That(results.Select(x => x.Sources.Single().Coalesced).Order().ToArray()).IsEquivalentTo(new[] { false, true });
        await Assert.That(results.All(x => x.Succeeded)).IsTrue();
    }

    [Test]
    public async Task DeletingABuildReportsBytesAndReappearanceAndDryRunKeepsIt()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var dry = await host.SendAsync(HttpMethod.Delete, "api/builds/main/100/1?dryRun=true");
        var dryBody = await JsonAsync(dry);
        await Assert.That(dry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(dryBody.GetProperty("dryRun").GetBoolean()).IsTrue();
        await Assert.That(dryBody.GetProperty("bytesReclaimed").GetInt64()).IsGreaterThan(0);
        using var stillThere = await host.SendAsync(HttpMethod.Get, "api/builds", csrfHeader: false);
        await Assert.That((await JsonAsync(stillThere)).EnumerateArray().Single().GetProperty("cachedState").GetString())
            .IsEqualTo("cached");

        using var response = await host.SendAsync(HttpMethod.Delete, "api/builds/main/100/1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        await Assert.That(body.GetProperty("removedCount").GetInt32()).IsEqualTo(1);
        await Assert.That(body.GetProperty("bytesReclaimed").GetInt64()).IsEqualTo(dryBody.GetProperty("bytesReclaimed").GetInt64());
        await Assert.That(body.GetProperty("anyWillReappear").GetBoolean()).IsTrue();
        var removed = body.GetProperty("builds").EnumerateArray().Single();
        await Assert.That(removed.GetProperty("willReappear").GetBoolean()).IsTrue();
        await Assert.That(removed.GetProperty("reappearance").GetString()).IsEqualTo("WillReappear");
        using var after = await host.SendAsync(HttpMethod.Get, "api/builds", csrfHeader: false);
        await Assert.That((await JsonAsync(after)).EnumerateArray().Single().GetProperty("cachedState").GetString())
            .IsEqualTo("notCached");
    }

    [Test]
    public async Task BulkRemovalVariantsDelegateToTheEvictionService()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var closed = await host.SendAsync(HttpMethod.Delete, "api/closed-pull-requests?closedForDays=0");
        await Assert.That(closed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(closed)).GetProperty("removedCount").GetInt32()).IsEqualTo(0);

        using var pullRequest = await host.SendAsync(HttpMethod.Delete, "api/pull-requests/main/755");
        await Assert.That(pullRequest.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(pullRequest)).GetProperty("removedCount").GetInt32()).IsEqualTo(1);

        await host.PopulateAsync();
        using var source = await host.SendAsync(HttpMethod.Delete, "api/sources/main/cache");
        await Assert.That(source.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await JsonAsync(source);
        await Assert.That(body.GetProperty("bytesReclaimed").GetInt64()).IsGreaterThan(0);

        using var unknown = await host.SendAsync(HttpMethod.Delete, "api/sources/nope/cache");
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound);
        using var invalid = await host.SendAsync(HttpMethod.Delete, "api/closed-pull-requests?closedForDays=-1");
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task StateChangingRequestsWithoutTheCustomHeaderAreRejectedAndChangeNothing()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();
        var listings = host.GitHub.RunListings;

        using var refresh = await host.SendAsync(HttpMethod.Post, "api/refresh", csrfHeader: false);
        await AssertProblemAsync(refresh, HttpStatusCode.Forbidden);
        using var delete = await host.SendAsync(HttpMethod.Delete, "api/builds/main/100/1", csrfHeader: false);
        await AssertProblemAsync(delete, HttpStatusCode.Forbidden);
        using var form = await host.SendAsync(HttpMethod.Post, "api/refresh", csrfHeader: false,
            request => request.Content = new FormUrlEncodedContent([new("source", "main")]));
        await AssertProblemAsync(form, HttpStatusCode.Forbidden);

        await Assert.That(host.GitHub.RunListings).IsEqualTo(listings);
        using var builds = await host.SendAsync(HttpMethod.Get, "api/builds", csrfHeader: false);
        await Assert.That((await JsonAsync(builds)).EnumerateArray().Single().GetProperty("cachedState").GetString())
            .IsEqualTo("cached");
    }

    [Test]
    public async Task CrossSiteOriginsAndForeignHostsAreRejected()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var origin = await host.SendAsync(HttpMethod.Delete, "api/builds/main/100/1",
            configure: request => request.Headers.Add("Origin", "https://evil.example"));
        await AssertProblemAsync(origin, HttpStatusCode.Forbidden);
        using var otherPort = await host.SendAsync(HttpMethod.Post, "api/refresh",
            configure: request => request.Headers.Add("Origin", "http://127.0.0.1:1"));
        await AssertProblemAsync(otherPort, HttpStatusCode.Forbidden);
        using var nullOrigin = await host.SendAsync(HttpMethod.Post, "api/refresh",
            configure: request => request.Headers.Add("Origin", "null"));
        await AssertProblemAsync(nullOrigin, HttpStatusCode.Forbidden);
        using var site = await host.SendAsync(HttpMethod.Post, "api/refresh",
            configure: request => request.Headers.Add("Sec-Fetch-Site", "cross-site"));
        await AssertProblemAsync(site, HttpStatusCode.Forbidden);
        using var foreignHost = await host.SendAsync(HttpMethod.Get, "api/sources", csrfHeader: false,
            request => request.Headers.Host = "rebind.example");
        await AssertProblemAsync(foreignHost, HttpStatusCode.Forbidden);

        using var builds = await host.SendAsync(HttpMethod.Get, "api/builds", csrfHeader: false);
        await Assert.That((await JsonAsync(builds)).EnumerateArray().Single().GetProperty("cachedState").GetString())
            .IsEqualTo("cached");

        var sameOrigin = host.App.Urls.Single();
        using var accepted = await host.SendAsync(HttpMethod.Post, "api/refresh", configure: request =>
        {
            request.Headers.Add("Origin", sameOrigin);
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
        });
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task UnknownApiRoutesReturnProblemDetailsAndBuildsStaysAvailable()
    {
        using var fixture = new PackageRewriteFixture();
        await using var host = await Host.StartAsync(fixture);
        await host.PopulateAsync();

        using var unknown = await host.SendAsync(HttpMethod.Get, "api/nope", csrfHeader: false);
        await AssertProblemAsync(unknown, HttpStatusCode.NotFound);
        using var badRoute = await host.SendAsync(HttpMethod.Delete, "api/builds/main/abc/1");
        await AssertProblemAsync(badRoute, HttpStatusCode.NotFound);

        using var builds = await host.SendAsync(HttpMethod.Get, "builds", csrfHeader: false);
        await Assert.That(builds.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(builds)).ValueKind).IsEqualTo(JsonValueKind.Array);
    }
}
