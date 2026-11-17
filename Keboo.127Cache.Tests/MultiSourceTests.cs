using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Keboo.LocalCache;
using Keboo.LocalCache.Testing;

namespace Keboo.LocalCache.Tests;

public sealed class MultiSourceTests
{
    private sealed class SharedFactory(IGitHubClient github, string? failingSource = null) : IBuildSourceFactory
    {
        public IBuildSource Create(ProxyOptions source) => source.Name == failingSource
            ? throw new IOException("Synthetic source startup failure.")
            : new GitHubActionsBuildSource(source, github);
    }

    private static async Task<WebApplication> StartAsync(PackageRewriteFixture fixture, FixtureGitHub github,
        CollectingLogger<PackageCatalog> logger, string? failingSource = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.DirectoryPath });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:CacheDirectory"] = Path.Combine(fixture.DirectoryPath, "cache"),
            ["Sources:0:Name"] = "first", ["Sources:0:Repository"] = "org/first",
            ["Sources:0:Workflow"] = "build.yml", ["Sources:0:Artifact"] = "packages",
            ["Sources:1:Name"] = "second", ["Sources:1:Repository"] = "org/second",
            ["Sources:1:Workflow"] = "build.yml", ["Sources:1:Artifact"] = "packages"
        });
        builder.Logging.ClearProviders();
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IBuildSourceFactory>(new SharedFactory(github, failingSource));
        builder.Services.AddSingleton<ILogger<PackageCatalog>>(logger);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync();
        return app;
    }

    [Test]
    public async Task EachSourceGetsItsOwnDirectoryAndCollidingVersionsAreLoggedAndServedOnce()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        var logger = new CollectingLogger<PackageCatalog>();
        await using var app = await StartAsync(fixture, github, logger);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var catalog = app.Services.GetRequiredService<PackageCatalog>();

        using var response = await CatalogTestSynchronization.GetWhenSettledAsync(client, catalog, "flat/acme.core/index.json");
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        using var index = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(index.RootElement.GetProperty("versions").GetArrayLength()).IsEqualTo(1);

        await Assert.That(catalog.SourceNames.ToArray()).IsEquivalentTo(new[] { "first", "second" });
        var root = Path.Combine(fixture.DirectoryPath, "cache", "sources");
        await Assert.That(Directory.Exists(Path.Combine(root, "first"))).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(root, "second"))).IsTrue();
        await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning
            && x.Message.Contains("first", StringComparison.Ordinal)
            && x.Message.Contains("second", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task HealthReportsEverySource()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        await using var app = await StartAsync(fixture, github, new CollectingLogger<PackageCatalog>());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var catalog = app.Services.GetRequiredService<PackageCatalog>();
        using var _ = await CatalogTestSynchronization.GetWhenSettledAsync(client, catalog, "flat/acme.core/index.json");

        var health = catalog.GetHealth();
        await Assert.That(health.Sources!.Select(x => x.Source).ToArray()).IsEquivalentTo(new[] { "first", "second" });
    }

    [Test]
    public async Task FailedSourceDoesNotHideHealthySourcePackages()
    {
        using var fixture = new PackageRewriteFixture();
        var github = await fixture.CreateGitHubAsync();
        await using var app = await StartAsync(fixture, github, new CollectingLogger<PackageCatalog>(), "first");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
        var catalog = app.Services.GetRequiredService<PackageCatalog>();
        using var response = await CatalogTestSynchronization.GetWhenSettledAsync(client, catalog, "flat/acme.core/index.json");
        response.EnsureSuccessStatusCode();
        using var index = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(index.RootElement.GetProperty("versions").GetArrayLength()).IsEqualTo(1);
        var health = catalog.GetHealth();
        await Assert.That(health.Status).IsEqualTo("degraded");
        await Assert.That(health.Sources!.Single(x => x.Source == "first").Error).Contains("Synthetic source startup failure");
    }

    [Test]
    public async Task LegacyCacheIsMovedToTheMatchingSourceOnly()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "artifacts"));
            await File.WriteAllTextAsync(Path.Combine(root, "runs.json"), """{"repository":"org/second","workflow":"build.yml"}""");
            await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "1.zip"), "x");
            var sources = new[]
            {
                new ProxyOptions { Name = "first", Repository = "org/first", Workflow = "build.yml", CacheDirectory = CacheLayout.SourceDirectory(root, "first") },
                new ProxyOptions { Name = "second", Repository = "org/second", Workflow = "build.yml", CacheDirectory = CacheLayout.SourceDirectory(root, "second") }
            };

            CacheLayout.MigrateLegacy(root, sources, new CollectingLogger<PackageCatalog>());

            await Assert.That(File.Exists(Path.Combine(root, "sources", "second", "runs.json"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(root, "sources", "second", "artifacts", "1.zip"))).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(root, "sources", "first"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(root, "runs.json"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task LegacyCacheMatchingNoSourceIsLeftUntouched()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "runs.json"), """{"repository":"org/other","workflow":"build.yml"}""");
            var logger = new CollectingLogger<PackageCatalog>();
            var sources = new[]
            {
                new ProxyOptions { Name = "first", Repository = "org/first", Workflow = "build.yml", CacheDirectory = CacheLayout.SourceDirectory(root, "first") }
            };

            CacheLayout.MigrateLegacy(root, sources, logger);

            await Assert.That(File.Exists(Path.Combine(root, "runs.json"))).IsTrue();
            await Assert.That(logger.Messages.Any(x => x.Level == LogLevel.Warning)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
