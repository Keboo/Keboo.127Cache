using Keboo.LocalCache.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Keboo.LocalCache.Tests;

public class CacheEvictionTests
{
    private sealed class ToggleGitHub(FixtureGitHub inner) : IGitHubClient
    {
        public bool PullRequestOpen { get; set; } = true;
        public bool ArtifactsExpired { get; set; }
        public bool FailDiscovery { get; set; }
        public TaskCompletionSource? DownloadStarted { get; set; }
        public TaskCompletionSource? ReleaseDownload { get; set; }

        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
            FailDiscovery ? throw new HttpRequestException("offline")
            : Task.FromResult<IReadOnlyList<PullRequestInfo>>(PullRequestOpen ? [new(755)] : []);

        public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) =>
            FailDiscovery ? throw new HttpRequestException("offline") : inner.GetRunsAsync(cancellationToken);

        public async Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken) =>
            (await inner.GetArtifactsAsync(runId, cancellationToken)).Select(x => x with { Expired = ArtifactsExpired }).ToArray();

        public async Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
        {
            DownloadStarted?.TrySetResult();
            if (ReleaseDownload is not null) await ReleaseDownload.Task.WaitAsync(cancellationToken);
            await inner.DownloadAsync(artifact, destination, cancellationToken);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required PackageRewriteFixture Fixture { get; init; }
        public required FixtureGitHub Inner { get; init; }
        public required ToggleGitHub GitHub { get; init; }
        public required ProxyOptions Options { get; init; }
        public required PackageCatalog Catalog { get; init; }
        public required CacheEvictionService Service { get; init; }
        public required Clock Clock { get; init; }
        public string Cache => Options.CacheDirectory;

        public static async Task<Harness> CreateAsync(PackageRewriteFixture fixture, int? evictAfterDays = null)
        {
            var inner = await fixture.CreateGitHubAsync();
            var github = new ToggleGitHub(inner);
            var clock = new Clock();
            var options = new ProxyOptions
            {
                Repository = "fixture/project",
                CacheDirectory = Path.Combine(fixture.DirectoryPath, "cache"),
                EvictClosedPullRequestsAfterDays = evictAfterDays,
                TimeProvider = clock
            };
            var artifacts = new ArtifactCache(options, github);
            var catalog = new PackageCatalog(options, github, new MemoryCache(new MemoryCacheOptions()), artifacts,
                new PackageCache(options, artifacts));
            return new Harness
            {
                Fixture = fixture, Inner = inner, GitHub = github, Options = options, Catalog = catalog, Clock = clock,
                Service = new CacheEvictionService(catalog, options)
            };
        }

        public Task<PublishedPackage[]> PopulateAsync() =>
            CatalogTestSynchronization.WhenSettledAsync(Catalog, () => Catalog.GetPackagesAsync(CancellationToken.None));

        public ValueTask DisposeAsync() => Catalog.DisposeAsync();
    }

    [Test]
    public async Task RemovedOpenPullRequestBuildIsDownloadedAgainWithTheSameVersion()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        var before = await harness.PopulateAsync();
        var downloads = harness.Inner.Downloads;
        var usage = await harness.Service.GetDiskUsageAsync();
        await Assert.That(usage.Sources.Single().Builds.Single().TotalBytes).IsGreaterThan(0);

        var removal = await harness.Service.RemoveBuildAsync("", 100, 1);

        var build = removal.Builds.Single();
        await Assert.That(build.WillReappear).IsTrue();
        await Assert.That(build.BytesReclaimed).IsGreaterThan(0);
        await Assert.That(removal.BytesReclaimed).IsEqualTo(build.BytesReclaimed);
        await Assert.That(Directory.Exists(Path.Combine(harness.Cache, "packages", "discovered", $"v{CacheSchema.Version}", "755.100.1.200"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(harness.Cache, "artifacts", "200.zip"))).IsFalse();
        await Assert.That((await harness.Service.GetDiskUsageAsync()).Sources.Single().Builds).IsEmpty();

        var after = await harness.PopulateAsync();

        await Assert.That(harness.Inner.Downloads).IsGreaterThan(downloads);
        await Assert.That(after.Select(x => x.Build.Version).ToArray()).IsEquivalentTo(before.Select(x => x.Build.Version).ToArray());
    }

    [Test]
    public async Task RemovedClosedPullRequestBuildIsNotRestored()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();
        harness.GitHub.PullRequestOpen = false;

        var removal = await harness.Service.RemovePullRequestAsync("", 755);

        await Assert.That(removal.Builds.Single().Reappearance).IsEqualTo(ReappearanceOutcome.PullRequestClosed);
        await Assert.That(removal.AnyWillReappear).IsFalse();
        var downloads = harness.Inner.Downloads;
        await Assert.That((await harness.Catalog.GetBuildsAsync(CancellationToken.None)).Length).IsEqualTo(0);
        await Assert.That(harness.Inner.Downloads).IsEqualTo(downloads);
        await Assert.That(Directory.Exists(Path.Combine(harness.Cache, "packages", "discovered", $"v{CacheSchema.Version}", "755.100.1.200"))).IsFalse();
    }

    [Test]
    public async Task RemovingABuildWhoseArtifactsExpiredReportsThatItIsPermanent()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();
        harness.GitHub.ArtifactsExpired = true;

        var removal = await harness.Service.RemoveBuildAsync("", 100, 1);

        await Assert.That(removal.Builds.Single().Reappearance).IsEqualTo(ReappearanceOutcome.ArtifactExpired);
        await Assert.That(removal.AnyPermanent).IsTrue();
        await Assert.That(removal.AnyWillReappear).IsFalse();
    }

    [Test]
    public async Task DryRunReportsTheSameBytesWithoutDeletingAnything()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();

        var preview = await harness.Service.RemoveBuildAsync("", 100, 1, dryRun: true);
        await Assert.That(File.Exists(Path.Combine(harness.Cache, "artifacts", "200.zip"))).IsTrue();
        var actual = await harness.Service.RemoveBuildAsync("", 100, 1);

        await Assert.That(preview.DryRun).IsTrue();
        await Assert.That(preview.BytesReclaimed).IsGreaterThan(0);
        await Assert.That(preview.BytesReclaimed).IsEqualTo(actual.BytesReclaimed);
    }

    [Test]
    public async Task RemovingAnUncachedBuildIsIdempotentAndAnUnknownSourceIsRejected()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();

        var removal = await harness.Service.RemoveBuildAsync("", 999, 1);

        await Assert.That(removal.Builds).IsEmpty();
        await Assert.That(removal.BytesReclaimed).IsEqualTo(0);
        await Assert.That(async () => await harness.Service.RemoveBuildAsync("missing", 100, 1)).ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task RemovalWaitsForAnInFlightDownloadAndLeavesConsistentState()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        harness.GitHub.DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.GitHub.ReleaseDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var population = Task.Run(async () =>
        {
            try { await harness.Catalog.GetPackagesAsync(CancellationToken.None); }
            catch (DiscoveryUnavailableException) { }
        });
        await harness.GitHub.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var removal = harness.Service.RemoveBuildAsync("", 100, 1);
        await Task.Delay(300);
        await Assert.That(removal.IsCompleted).IsFalse();

        harness.GitHub.ReleaseDownload.SetResult();
        var result = await removal.WaitAsync(TimeSpan.FromSeconds(30));
        await population;
        await harness.Catalog.WaitForBackgroundWorkAsync(CancellationToken.None);

        await Assert.That(result.Builds.Single().BytesReclaimed).IsGreaterThan(0);
        await Assert.That((await harness.Service.GetDiskUsageAsync()).Sources.Single().Builds).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(harness.Cache, "artifacts", "200.zip"))).IsFalse();
    }

    [Test]
    public async Task PartialPackageDirectoryIsReportedAndCleanedUp()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();
        var directory = Path.Combine(harness.Cache, "packages", "discovered", $"v{CacheSchema.Version}", "755.100.1.200");
        File.Delete(Path.Combine(directory, "packages.json"));

        var usage = await harness.Service.GetSourceDiskUsageAsync("");
        await Assert.That(usage.Builds.Single().IsPartial).IsTrue();
        await Assert.That(usage.BuildBytes).IsGreaterThan(0);
        await Assert.That(usage.TotalBytes).IsGreaterThanOrEqualTo(usage.BuildBytes);

        await harness.Service.RemoveBuildAsync("", 100, 1);

        await Assert.That(Directory.Exists(directory)).IsFalse();
        await Assert.That(await harness.PopulateAsync()).IsNotEmpty();
    }

    [Test]
    public async Task RemoveSourceEmptiesTheCacheIncludingUnreferencedFiles()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();
        var stray = Path.Combine(harness.Cache, "artifacts", "999.zip");
        await File.WriteAllTextAsync(stray, new string('x', 1000));

        var removal = await harness.Service.RemoveSourceAsync("");

        await Assert.That(File.Exists(stray)).IsFalse();
        await Assert.That(removal.BytesReclaimed).IsGreaterThan(removal.Builds.Sum(x => x.BytesReclaimed));
        await Assert.That(Directory.EnumerateFiles(Path.Combine(harness.Cache, "artifacts")).Any()).IsFalse();
    }

    [Test]
    public async Task RetentionPolicyEvictsOnlyPullRequestsClosedLongEnough()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture, evictAfterDays: 7);
        await harness.PopulateAsync();

        await Assert.That((await harness.Service.ApplyRetentionPolicyAsync()).Builds).IsEmpty();
        harness.GitHub.PullRequestOpen = false;
        await Assert.That((await harness.Service.ApplyRetentionPolicyAsync()).Builds).IsEmpty();

        harness.Clock.Now += TimeSpan.FromDays(6);
        await Assert.That((await harness.Service.ApplyRetentionPolicyAsync()).Builds).IsEmpty();
        harness.Clock.Now += TimeSpan.FromDays(2);
        var result = await harness.Service.ApplyRetentionPolicyAsync();

        await Assert.That(result.Builds.Single().Reappearance).IsEqualTo(ReappearanceOutcome.PullRequestClosed);
        await Assert.That(result.BytesReclaimed).IsGreaterThan(0);
        await Assert.That((await harness.Service.GetDiskUsageAsync()).Sources.Single().Builds).IsEmpty();
    }

    [Test]
    public async Task RetentionPolicyIsOffByDefaultAndKeepsOpenPullRequests()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();
        harness.GitHub.PullRequestOpen = false;
        harness.Clock.Now += TimeSpan.FromDays(365);

        await Assert.That((await harness.Service.ApplyRetentionPolicyAsync()).Builds).IsEmpty();
        await Assert.That((await harness.Service.GetDiskUsageAsync()).Sources.Single().Builds).IsNotEmpty();
    }

    [Test]
    public async Task ClosedPullRequestRemovalDoesNothingWhenGitHubCannotBeQueried()
    {
        using var fixture = new PackageRewriteFixture();
        await using var harness = await Harness.CreateAsync(fixture);
        await harness.PopulateAsync();
        harness.GitHub.FailDiscovery = true;

        var result = await harness.Service.RemoveClosedPullRequestsAsync();

        await Assert.That(result.Builds).IsEmpty();
        await Assert.That((await harness.Service.GetDiskUsageAsync()).Sources.Single().Builds).IsNotEmpty();

        var single = await harness.Service.RemoveBuildAsync("", 100, 1, dryRun: true);
        await Assert.That(single.Builds.Single().Reappearance).IsEqualTo(ReappearanceOutcome.Unknown);
    }

    [Test]
    public async Task EvictionSettingIsParsedAndValidated()
    {
        static CacheConfiguration Load(string json) =>
            CacheConfigurationLoader.Load(new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build());

        await Assert.That(Load("{}").Server.EvictClosedPullRequestsAfterDays).IsNull();
        await Assert.That(Load("""{ "Server": { "EvictClosedPullRequestsAfterDays": 14 } }""").Server.EvictClosedPullRequestsAfterDays).IsEqualTo(14);
        await Assert.That(ProxyOptions.FromConfiguration(Load("""{ "Server": { "EvictClosedPullRequestsAfterDays": 3 } }""")).EvictClosedPullRequestsAfterDays).IsEqualTo(3);
        await Assert.That(() => Load("""{ "Server": { "EvictClosedPullRequestsAfterDays": 0 } }"""))
            .Throws<Microsoft.Extensions.Options.OptionsValidationException>();
    }
}
