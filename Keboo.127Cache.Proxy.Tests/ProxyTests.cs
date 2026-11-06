using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using NuGet.Packaging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using Keboo._127Cache;

using TUnit.Assertions.Enums;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keboo._127Cache.Proxy.Tests;

public sealed class ProxyTests : IDisposable
{
    private static CancellationToken Cancellation => TestContext.Current!.Execution.CancellationToken;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pr-nuget-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github;
    private readonly ProxyOptions _options;
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly ArtifactCache _artifacts;
    private PackageCatalog CreateCatalog() => new(_options, _github, _memoryCache, _artifacts, CreatePackageCache());
    private PackageCache CreatePackageCache() => new(_options, _artifacts);

    public ProxyTests()
    {
        Directory.CreateDirectory(_directory);
        _github = new FakeGitHub(_directory);
        _options = new ProxyOptions
        {
            CacheDirectory = Path.Combine(_directory, "cache"),
            PullRequests = [755]
        };
        _artifacts = new ArtifactCache(_options, _github);
    }

    [Test]
    public async Task VersionsAreDistinctNormalizedPrereleases()
    {
        var first = PackageRewriter.VersionFor("1.3.67-gabcdef0+metadata", 755, 100, 1, 200);
        await Assert.That(first).IsEqualTo("1.3.67-pr.755.run.100.attempt.1.artifact.200");
        await Assert.That(NuGetVersion.Parse(first).ToNormalizedString()).IsEqualTo(first);
        await Assert.That(PackageRewriter.VersionFor("1.3.67-gabcdef0", 755, 100, 2, 201)).IsNotEqualTo(first);
        await Assert.That(PackageRewriter.VersionFor("1.3.67-gabcdef0", 756, 101, 1, 202)).IsNotEqualTo(first);
    }

    [Test]
    public async Task RewriterPreservesPayloadAndDependencies()
    {
        var source = Path.Combine(_directory, "original.nupkg");
        var destination = Path.Combine(_directory, "rewritten.nupkg");
        FakeGitHub.WritePackage(source, "Velopack");
        PackageRewriter.Rewrite(source, destination, "Velopack", FakeGitHub.OriginalVersion, "1.3.67-pr.755");
        using var reader = new PackageArchiveReader(destination);
        await Assert.That(reader.GetIdentity().Id).IsEqualTo("Velopack");
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo("1.3.67-pr.755");
        var group = await Assert.That(reader.NuspecReader.GetDependencyGroups()).HasSingleItem();
        var dependency = await Assert.That(group.Packages).HasSingleItem();
        await Assert.That(dependency.Id).IsEqualTo("Newtonsoft.Json");
        using var original = ZipFile.OpenRead(source);
        using var rewritten = ZipFile.OpenRead(destination);
        foreach (var entry in original.Entries.Where(x => !x.FullName.EndsWith(".nuspec")))
        {
            var result = rewritten.GetEntry(entry.FullName)!;
            await Assert.That(result.ExternalAttributes).IsEqualTo(entry.ExternalAttributes);
            await Assert.That(result.LastWriteTime).IsEqualTo(entry.LastWriteTime);
            using var input = entry.Open();
            using var output = result.Open();
            var originalHash = SHA256.HashData(input);
            var rewrittenHash = SHA256.HashData(output);
            await Assert.That(rewrittenHash).IsEquivalentTo(originalHash, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task SignedAndMismatchedPackagesAreRejected()
    {
        var source = Path.Combine(_directory, "signed.nupkg");
        FakeGitHub.WritePackage(source, "Velopack");
        await Assert.That(() => PackageRewriter.Rewrite(source, source + ".wrong", "Other",
            FakeGitHub.OriginalVersion, "1.0.0-test")).ThrowsExactly<InvalidDataException>();
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update)) zip.CreateEntry(".signature.p7s");
        await Assert.That(() => PackageRewriter.Rewrite(source, source + ".out", "Velopack",
            FakeGitHub.OriginalVersion, "1.0.0-test")).ThrowsExactly<InvalidDataException>();
    }

    [Test]
    public async Task MetadataWarmupPersistsArtifactListingsWithoutDownloadingContents()
    {
        var catalog = CreateCatalog();
        _github.FailDownloads = true;
        var run = await Assert.That(await catalog.GetRunArtifactsAsync(Cancellation)).HasSingleItem();
        await Assert.That(run.RunId).IsEqualTo(100);
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(1);
        await Assert.That(_github.RunQueries).IsEqualTo(1);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(_github.VersionDownloads).IsEqualTo(0);
        await Assert.That(_github.PackageDownloads).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(_options.CacheDirectory, "artifacts"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_options.CacheDirectory, "catalog.json"))).IsFalse();
        var saved = JsonSerializer.Deserialize<RunArtifactsFile>(
            File.ReadAllText(Path.Combine(_options.CacheDirectory, "runs.json")), JsonSettings.Options)!;
        var savedRun = await Assert.That(saved.Runs).HasSingleItem();
        await Assert.That(savedRun).IsEqualTo(run);

        var restoredRun = await Assert.That(await CreateCatalog().GetRunArtifactsAsync(Cancellation)).HasSingleItem();
        await Assert.That(restoredRun).IsEqualTo(run);
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(2);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        _github.FailDownloads = false;
        await Assert.That(await catalog.GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(2);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
        await Assert.That(_github.PackageDownloads).IsEqualTo(0);
    }

    [Test]
    public async Task CatalogFiltersRunsAndPersistsImmutableIdentities()
    {
        var catalog = CreateCatalog();
        var builds = await catalog.GetBuildsAsync(Cancellation);
        var build = await Assert.That(builds).HasSingleItem();
        await Assert.That(build.RunId).IsEqualTo(100);
        await Assert.That(build.PullRequest).IsEqualTo(755);
        await Assert.That(build.OriginalVersion).IsEqualTo(FakeGitHub.OriginalVersion);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
        await catalog.GetBuildsAsync(Cancellation);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
        var restored = await CreateCatalog().GetBuildsAsync(Cancellation);
        await ProxyAssertions.EqualRecords(restored, builds);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task PackageCacheDownloadsOnceAndReusesLocalFiles()
    {
        var build = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        var cache = CreatePackageCache();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => cache.GetAsync("Velopack", build, Cancellation)));
        foreach (var result in results)
            await Assert.That(result).IsEqualTo(results[0]);
        await Assert.That(_github.PackageDownloads).IsEqualTo(1);
        var tool = await cache.GetAsync("vpk", build, Cancellation);
        using var reader = new PackageArchiveReader(tool);
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(build.Version);
        _github.FailDownloads = true;
        await Assert.That(await CreatePackageCache().GetAsync("Velopack", build, Cancellation)).IsEqualTo(results[0]);
    }

    [Test]
    public async Task CorruptArtifactNeverPublishesPartialPackages()
    {
        var build = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        var cache = CreatePackageCache();
        _github.CorruptDownload = true;
        await Assert.That(async () => await cache.GetAsync("vpk", build, Cancellation)).ThrowsExactly<InvalidDataException>();
        await Assert.That(Directory.Exists(Path.Combine(_options.CacheDirectory, "packages", "discovered"))).IsFalse();
        await Assert.That(Directory.GetDirectories(_options.CacheDirectory, "staging-*")).IsEmpty();
        _github.CorruptDownload = false;
        await Assert.That(File.Exists(await cache.GetAsync("vpk", build, Cancellation))).IsTrue();
    }

    [Test]
    public async Task PackageInventoryIsDiscoveredAndPersistedWithoutConfiguration()
    {
        var catalog = CreateCatalog();
        var packages = await catalog.GetPackagesAsync(Cancellation);
        await Assert.That(packages.Select(x => x.Id).Order().ToArray())
            .IsEquivalentTo(new[] { "Example.Plugin", "Velopack", "Velopack.Build", "vpk" }, CollectionOrdering.Matching);
        var plugin = await Assert.That(packages.Where(x => x.Id == "Example.Plugin")).HasSingleItem();
        await Assert.That(plugin.Build.Version).StartsWith("2.0.0-pr.", StringComparison.CurrentCulture);
        var cache = CreatePackageCache();
        using var reader = new PackageArchiveReader(await cache.GetAsync(plugin.Id, plugin.Build, Cancellation));
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(plugin.Build.Version);
        var group = await Assert.That(reader.NuspecReader.GetDependencyGroups()).HasSingleItem();
        var dependency = await Assert.That(group.Packages).HasSingleItem();
        await Assert.That(dependency.Id).IsEqualTo("Velopack");
        var library = await Assert.That(packages.Where(x => x.Id == "Velopack")).HasSingleItem();
        await Assert.That(dependency.VersionRange.ToNormalizedString())
            .IsEqualTo(VersionRange.Parse($"[{library.Build.Version}]").ToNormalizedString());
        _github.FailDownloads = true;
        var restored = await CreateCatalog().GetPackagesAsync(Cancellation);
        await ProxyAssertions.EqualRecords(restored, packages);
        await Assert.That(async () => await cache.GetAsync("missing", plugin.Build, Cancellation)).ThrowsExactly<InvalidDataException>();
        await Assert.That(_github.PackageDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task ExistingAllowlistCatalogAndPartialPackageCacheAreReusable()
    {
        var build = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        await JsonSettings.WriteAtomicAsync(Path.Combine(_options.CacheDirectory, "catalog.json"),
            new { _options.Repository, PackageIds = new[] { "Velopack", "vpk" }, Builds = new[] { build } }, Cancellation);
        var oldDirectory = Path.Combine(_options.CacheDirectory, "packages", build.Version);
        Directory.CreateDirectory(oldDirectory);
        File.WriteAllText(Path.Combine(oldDirectory, $"velopack.{build.Version}.nupkg"), "old cached package");
        var packages = await CreateCatalog().GetPackagesAsync(Cancellation);
        await Assert.That(packages).Contains(x => x.Id == "Velopack.Build");
        await Assert.That(packages).Contains(x => x.Id == "Example.Plugin");
    }

    [Test]
    public async Task PinnedExpiredRunFailsExplicitly()
    {
        _options.RunIds = [100];
        _github.Expired = true;
        await Assert.That(async () => await CreateCatalog().GetBuildsAsync(Cancellation)).ThrowsExactly<InvalidDataException>();
    }

    [Test]
    public async Task DiscoveryErrorsDoNotBecomeEmptySuccessfulFeeds()
    {
        _github.FailDiscovery = true;
        await Assert.That(async () => await CreateCatalog().GetBuildsAsync(Cancellation)).ThrowsExactly<IOException>();
    }

    [Test]
    public async Task MemoryCacheGatesGitHubQueriesAndRefreshesAfterEviction()
    {
        var catalog = CreateCatalog();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.GetBuildsAsync(Cancellation)));
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(1);
        await Assert.That(_github.RunQueries).IsEqualTo(1);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        _memoryCache.Compact(1);
        await catalog.GetBuildsAsync(Cancellation);
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(2);
        await Assert.That(_github.RunQueries).IsEqualTo(2);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task ClosedPullRequestsDisappearEvenWhenPinnedAndCached()
    {
        _options.RunIds = [100];
        var catalog = CreateCatalog();
        var build = await Assert.That(await catalog.GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        var cachedPackage = await CreatePackageCache().GetAsync("Velopack", build, Cancellation);
        _github.OpenPullRequests = [new(756)];
        _memoryCache.Compact(1);
        await Assert.That(await catalog.GetBuildsAsync(Cancellation)).IsEmpty();
        await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).IsEmpty();
        var saved = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(Path.Combine(_options.CacheDirectory, "catalog.json")),
            JsonSettings.Options)!;
        var cachedBuild = await Assert.That(saved.Builds).HasSingleItem();
        await Assert.That(cachedBuild.VersionArtifact!.Id).IsEqualTo(build.VersionArtifact!.Id);
        await Assert.That(cachedBuild.Packages.Id).IsEqualTo(build.Packages.Id);
        await Assert.That(File.Exists(cachedPackage)).IsTrue();

        _github.OpenPullRequests = [new(755), new(756)];
        _memoryCache.Compact(1);
        var reopenedBuild = await Assert.That(await catalog.GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(reopenedBuild.Version).IsEqualTo(build.Version);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task OnlyCompletedSuccessfulRunsOfOpenPullRequestsAreDiscovered()
    {
        _options.PullRequests = [];
        _github.OpenPullRequests = [new(755)];
        _github.Runs = [
            new(100, 1, "good", "completed", "success", "pull_request", [new(755)]),
            new(101, 1, "running", "in_progress", null, "pull_request", [new(755)]),
            new(102, 1, "queued", "queued", null, "pull_request", [new(755)]),
            new(103, 1, "failed", "completed", "failure", "pull_request", [new(755)]),
            new(104, 1, "closed", "completed", "success", "pull_request", [new(756)]),
            new(105, 1, "push", "completed", "success", "push", [new(755)])
        ];
        var build = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(build.RunId).IsEqualTo(100);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
    }

    [Test]
    public async Task ForkAndSameRepositoryPullRequestsAreServedWithoutPins()
    {
        _github.OpenPullRequests = JsonSerializer.Deserialize<PullRequestInfo[]>(
            """
            [
              {"number":755,"head":{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ref":"fork-feature","repo":{"full_name":"alice/widgets"}}},
              {"number":756,"head":{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","ref":"same-feature","repo":{"full_name":"owner/widgets"}}},
              {"number":757,"head":{"sha":"cccccccccccccccccccccccccccccccccccccccc","ref":"explicit-feature","repo":{"full_name":"owner/widgets"}}}
            ]
            """, JsonSettings.Options)!;
        _github.Runs = JsonSerializer.Deserialize<RunsResponse>(
            """
            {"workflow_runs":[
              {"id":100,"run_attempt":1,"head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","head_branch":"fork-feature","head_repository":{"full_name":"alice/widgets"},"status":"completed","conclusion":"success","event":"pull_request","pull_requests":[]},
              {"id":101,"run_attempt":1,"head_sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","head_branch":"same-feature","head_repository":{"full_name":"owner/widgets"},"status":"completed","conclusion":"success","event":"pull_request","pull_requests":[]},
              {"id":102,"run_attempt":1,"head_sha":"cccccccccccccccccccccccccccccccccccccccc","head_branch":"explicit-feature","head_repository":{"full_name":"owner/widgets"},"status":"completed","conclusion":"success","event":"pull_request","pull_requests":[{"number":757}]}
            ]}
            """, JsonSettings.Options)!.Runs;

        var forkHead = _github.OpenPullRequests[0].Head;
        await Assert.That(forkHead).IsNotNull();
        await Assert.That(forkHead!.Sha).IsEqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await Assert.That(forkHead.Branch).IsEqualTo("fork-feature");
        await Assert.That(forkHead.Repository?.FullName).IsEqualTo("alice/widgets");
        await Assert.That(_github.Runs[0].HeadBranch).IsEqualTo("fork-feature");
        await Assert.That(_github.Runs[0].HeadRepository?.FullName).IsEqualTo("alice/widgets");

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:0",
            ["Proxy:Repository"] = "fixture/project",
            ["Proxy:CacheDirectory"] = _options.CacheDirectory
        });
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(_github);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(Cancellation);
        var baseUrl = await Assert.That(app.Urls).HasSingleItem();
        var options = app.Services.GetRequiredService<ProxyOptions>();
        options.BaseUrl = baseUrl;
        await Assert.That(options.RunIds).IsEmpty();

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using var versions = JsonDocument.Parse(await client.GetStringAsync("/flat/velopack/index.json", Cancellation));
        var listed = versions.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()).ToArray();
        const string forkVersion = "1.3.67-pr.755.run.100.attempt.1.artifact.2";
        const string sameVersion = "1.3.67-pr.756.run.101.attempt.1.artifact.2";
        const string explicitVersion = "1.3.67-pr.757.run.102.attempt.1.artifact.2";
        await Assert.That(listed.Length).IsEqualTo(3);
        await Assert.That(listed).Contains(forkVersion);
        await Assert.That(listed).Contains(sameVersion);
        await Assert.That(listed).Contains(explicitVersion);

        var package = await client.GetByteArrayAsync(
            $"/flat/velopack/{forkVersion}/velopack.{forkVersion}.nupkg", Cancellation);
        using var reader = new PackageArchiveReader(new MemoryStream(package));
        await Assert.That(reader.GetIdentity().Id).IsEqualTo("Velopack");
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(forkVersion);

        _github.OpenPullRequests = _github.OpenPullRequests.Where(x => x.Number != 755).ToArray();
        var memoryCache = await ProxyAssertions.ExactMemoryCache(app.Services.GetRequiredService<IMemoryCache>());
        memoryCache.Compact(1);
        using var afterClose = JsonDocument.Parse(await client.GetStringAsync("/flat/velopack/index.json", Cancellation));
        var remaining = afterClose.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()).ToArray();
        await Assert.That(remaining.Length).IsEqualTo(2);
        await Assert.That(remaining).Contains(sameVersion);
        await Assert.That(remaining).Contains(explicitVersion);
        await app.StopAsync(Cancellation);
    }

    [Test]
    public async Task HeadMatchingUsesRepositoryAndBranchWithoutGuessingAmbiguousPullRequests()
    {
        _options.PullRequests = [];
        _github.OpenPullRequests = [
            new(755, new("shared", "feature", new("alice/widgets"))),
            new(756, new("shared", "feature", new("bob/widgets"))),
            new(757, new("shared", "duplicate", new("team/widgets"))),
            new(758, new("shared", "duplicate", new("team/widgets"))),
            new(759, new("unique", "topic", new("team/widgets"))),
            new(760, new("wrong-repository", "topic", new("correct/widgets"))),
            new(761, new("wrong-branch", "feature", new("correct/widgets")))
        ];
        _github.Runs = [
            new(101, 1, "SHARED", "completed", "success", "pull_request", [], "feature", new("ALICE/Widgets")),
            new(102, 1, "shared", "completed", "success", "pull_request", [], "feature", new("bob/widgets")),
            new(103, 1, "shared", "completed", "success", "pull_request", [], "duplicate", new("team/widgets")),
            new(104, 1, "shared", "completed", "success", "pull_request", []),
            new(105, 1, "unique", "completed", "success", "pull_request", []),
            new(106, 1, "wrong-repository", "completed", "success", "pull_request", [], "topic", new("other/widgets")),
            new(107, 1, "wrong-branch", "completed", "success", "pull_request", [], "Feature", new("correct/widgets")),
            new(108, 1, "unique", "completed", "success", "pull_request", [new(999)], "topic", new("team/widgets")),
            new(109, 1, "unique", "completed", "success", "push", [], "topic", new("team/widgets")),
            new(110, 1, "unique", "completed", "failure", "pull_request", [], "topic", new("team/widgets"))
        ];

        var runs = await CreateCatalog().GetRunArtifactsAsync(Cancellation);
        await Assert.That(runs.Select(x => $"{x.RunId}:{x.PullRequest}").ToArray())
            .IsEquivalentTo(new[] { "101:755", "102:756", "105:759" }, CollectionOrdering.Matching);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(3);
    }

    [Test]
    [Arguments("failure", false)]
    [Arguments("success", true)]
    public async Task PinnedForkRunRequiresSuccessfulUnexpiredArtifacts(string conclusion, bool expired)
    {
        _options.PullRequests = [999];
        _options.RunIds = [100];
        _github.OpenPullRequests = [new(755, new("fork-sha", "feature", new("alice/widgets")))];
        _github.Runs = [new(100, 1, "fork-sha", "completed", conclusion, "pull_request", [], "feature", new("alice/widgets"))];
        _github.Expired = expired;
        var catalog = CreateCatalog();
        await Assert.That(async () => await catalog.GetRunArtifactsAsync(Cancellation)).ThrowsExactly<InvalidDataException>();

        _github.Expired = false;
        _github.Runs[0] = _github.Runs[0] with { Conclusion = "success" };
        var run = await Assert.That(await catalog.GetRunArtifactsAsync(Cancellation)).HasSingleItem();
        await Assert.That(run.RunId).IsEqualTo(100);
        await Assert.That(run.PullRequest).IsEqualTo(755);
    }

    [Test]
    public async Task NoOpenPullRequestsMeansNoRunOrArtifactQueries()
    {
        _github.OpenPullRequests = [];
        await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).IsEmpty();
        await Assert.That(_github.RunQueries).IsEqualTo(0);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(0);
        await Assert.That(_github.VersionDownloads).IsEqualTo(0);
    }

    [Test]
    public async Task RawArtifactsAreReusedAfterCatalogOrRewrittenPackagesAreRemoved()
    {
        var build = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        var package = await CreatePackageCache().GetAsync("Velopack", build, Cancellation);
        Directory.Delete(Path.GetDirectoryName(package)!, recursive: true);
        File.Delete(Path.Combine(_options.CacheDirectory, "catalog.json"));
        _github.FailDownloads = true;
        var restored = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(restored).IsEqualTo(build);
        await Assert.That(File.Exists(await CreatePackageCache().GetAsync("Velopack", restored, Cancellation))).IsTrue();
        await Assert.That(_github.PackageDownloads).IsEqualTo(1);
        await Assert.That(_github.VersionDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task ExpiredArtifactsCanStillBeUsedFromDisk()
    {
        var build = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        await CreatePackageCache().GetAsync("Velopack", build, Cancellation);
        File.Delete(Path.Combine(_options.CacheDirectory, "catalog.json"));
        _github.Expired = true;
        _github.FailDownloads = true;
        var restored = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(restored.Version).IsEqualTo(build.Version);
        await Assert.That(restored.Packages.Id).IsEqualTo(build.Packages.Id);
    }

    [Test]
    public async Task FailedDiscoveryIsNotCached()
    {
        var catalog = CreateCatalog();
        _github.FailDiscovery = true;
        await Assert.That(async () => await catalog.GetBuildsAsync(Cancellation)).ThrowsExactly<IOException>();
        _github.FailDiscovery = false;
        await Assert.That(await catalog.GetBuildsAsync(Cancellation)).HasSingleItem();
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(2);
    }

    [Test]
    public async Task CacheCannotBeReusedForAnotherRepository()
    {
        await CreateCatalog().GetBuildsAsync(Cancellation);
        _options.Repository = "other/project";
        await Assert.That(() => CreateCatalog()).ThrowsExactly<InvalidDataException>();
    }

    [Test]
    public async Task HttpFeedHasConsistentIdentitiesAndDependencies()
    {
        _options.Repository = "fixture/project";
        _github.OpenPullRequests = [new(755)];
        _options.BaseUrl = "http://127.0.0.1:0";
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = _options.BaseUrl,
            ["Proxy:Repository"] = _options.Repository,
            ["Proxy:BaseUrl"] = _options.BaseUrl,
            ["Proxy:CacheDirectory"] = _options.CacheDirectory
        });
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(_github);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(Cancellation);
        _options.BaseUrl = await Assert.That(app.Urls).HasSingleItem();
        app.Services.GetRequiredService<ProxyOptions>().BaseUrl = _options.BaseUrl;
        await Assert.That(_github.OpenPullRequestQueries).IsEqualTo(1);
        await Assert.That(_github.RunQueries).IsEqualTo(1);
        await Assert.That(_github.ArtifactQueries).IsEqualTo(1);
        await Assert.That(_github.VersionDownloads).IsEqualTo(0);
        await Assert.That(_github.PackageDownloads).IsEqualTo(0);
        using var client = new HttpClient { BaseAddress = new Uri(_options.BaseUrl) };
        using var index = JsonDocument.Parse(await client.GetStringAsync("/v3/index.json", Cancellation));
        await Assert.That(index.RootElement.GetProperty("resources")[0].GetProperty("@type").GetString())
            .IsEqualTo("PackageBaseAddress/3.0.0");
        using var versions = JsonDocument.Parse(await client.GetStringAsync("/flat/velopack/index.json", Cancellation));
        var versionElement = await Assert.That(versions.RootElement.GetProperty("versions").EnumerateArray()).HasSingleItem();
        var version = versionElement.GetString();
        var package = await client.GetByteArrayAsync($"/flat/velopack/{version}/velopack.{version}.nupkg", Cancellation);
        using var reader = new PackageArchiveReader(new MemoryStream(package));
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
        using var buildVersions = JsonDocument.Parse(await client.GetStringAsync("/flat/velopack.build/index.json", Cancellation));
        var buildVersionElement = await Assert.That(buildVersions.RootElement.GetProperty("versions").EnumerateArray()).HasSingleItem();
        await Assert.That(buildVersionElement.GetString()).IsEqualTo(version);
        var buildPackage = await client.GetByteArrayAsync(
            $"/flat/velopack.build/{version}/velopack.build.{version}.nupkg", Cancellation);
        using var buildReader = new PackageArchiveReader(new MemoryStream(buildPackage));
        await Assert.That(buildReader.GetIdentity().Id).IsEqualTo("Velopack.Build");
        await Assert.That(buildReader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
        using var pluginVersions = JsonDocument.Parse(await client.GetStringAsync("/flat/example.plugin/index.json", Cancellation));
        var pluginVersionElement = await Assert.That(pluginVersions.RootElement.GetProperty("versions").EnumerateArray()).HasSingleItem();
        var pluginVersion = pluginVersionElement.GetString();
        await Assert.That(pluginVersion).StartsWith("2.0.0-pr.", StringComparison.CurrentCulture);
        using var pluginRegistration = JsonDocument.Parse(await client.GetStringAsync("/registration/example.plugin/index.json", Cancellation));
        var pluginEntry = pluginRegistration.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetProperty("catalogEntry");
        await Assert.That(pluginEntry.GetProperty("version").GetString()).IsEqualTo(pluginVersion);
        await Assert.That(pluginEntry.GetProperty("dependencyGroups")[0].GetProperty("dependencies")[0].GetProperty("range").GetString())
            .IsEqualTo(VersionRange.Parse($"[{version}]").ToNormalizedString());
        using var registration = JsonDocument.Parse(await client.GetStringAsync("/registration/velopack/index.json", Cancellation));
        var entry = registration.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetProperty("catalogEntry");
        await Assert.That(entry.GetProperty("version").GetString()).IsEqualTo(version);
        await Assert.That(entry.GetProperty("dependencyGroups")[0].GetProperty("dependencies")[0].GetProperty("id").GetString())
            .IsEqualTo("Newtonsoft.Json");
        var nuspec = await client.GetByteArrayAsync($"/flat/velopack/{version}/velopack.nuspec", Cancellation);
        var manifestBuild = await Assert.That(await CreateCatalog().GetBuildsAsync(Cancellation)).HasSingleItem();
        var cachedManifest = PackageRewriter.ReadManifest(await CreatePackageCache().GetAsync("velopack", manifestBuild, Cancellation));
        await Assert.That(nuspec).IsEquivalentTo(cachedManifest, CollectionOrdering.Matching);
        using var search = JsonDocument.Parse(await client.GetStringAsync("/query?q=velopack&prerelease=true&semVerLevel=2.0.0", Cancellation));
        await Assert.That(search.RootElement.GetProperty("totalHits").GetInt32()).IsEqualTo(2);
        await Assert.That(search.RootElement.GetProperty("data")[0].GetProperty("version").GetString()).IsEqualTo(version);
        using var stable = JsonDocument.Parse(await client.GetStringAsync("/query?prerelease=false", Cancellation));
        await Assert.That(stable.RootElement.GetProperty("totalHits").GetInt32()).IsEqualTo(0);
        using var tools = JsonDocument.Parse(await client.GetStringAsync(
            "/query?prerelease=true&semVerLevel=2.0.0&packageType=DotnetTool", Cancellation));
        var toolEntry = await Assert.That(tools.RootElement.GetProperty("data").EnumerateArray()).HasSingleItem();
        await Assert.That(toolEntry.GetProperty("id").GetString()).IsEqualTo("vpk");
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,
            $"/flat/vpk/{version}/vpk.{version}.nupkg"), Cancellation);
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await head.Content.ReadAsByteArrayAsync(Cancellation)).IsEmpty();
        await Assert.That((await client.GetAsync("/flat/other/index.json", Cancellation)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var source = Repository.Factory.GetCoreV3(new PackageSource(_options.BaseUrl + "/v3/index.json")
        {
            AllowInsecureConnections = true
        });
        var metadataResource = await source.GetResourceAsync<PackageMetadataResource>(Cancellation);
        await Assert.That(metadataResource).IsNotNull();
        using var context = new SourceCacheContext { NoCache = true };
        var metadata = await Assert.That(await metadataResource.GetMetadataAsync("Velopack", true, false, context,
            NullLogger.Instance, Cancellation)).HasSingleItem();
        await Assert.That(metadata.Identity.Version.ToNormalizedString()).IsEqualTo(version);
        var metadataGroup = await Assert.That(metadata.DependencySets).HasSingleItem();
        var metadataDependency = await Assert.That(metadataGroup.Packages).HasSingleItem();
        await Assert.That(metadataDependency.Id).IsEqualTo("Newtonsoft.Json");
        var searchResource = await source.GetResourceAsync<PackageSearchResource>(Cancellation);
        await Assert.That(searchResource).IsNotNull();
        var foundPackages = (await searchResource.SearchAsync("Velopack", new SearchFilter(includePrerelease: true),
            0, 20, NullLogger.Instance, Cancellation)).ToArray();
        await Assert.That(foundPackages.Length).IsEqualTo(2);
        var found = await Assert.That(foundPackages.Where(x => x.Identity.Id == "Velopack.Build")).HasSingleItem();
        await Assert.That(found.Identity.Version.ToNormalizedString()).IsEqualTo(version);
        var autocomplete = await source.GetResourceAsync<AutoCompleteResource>(Cancellation);
        await Assert.That(autocomplete).IsNotNull();
        await Assert.That(await autocomplete.IdStartsWith("Velo", true, NullLogger.Instance, Cancellation))
            .IsEquivalentTo(new[] { "Velopack", "Velopack.Build" }, CollectionOrdering.Matching);
        app.Services.GetRequiredService<ProxyOptions>().RunIds = [99];
        var memoryCache = await ProxyAssertions.ExactMemoryCache(app.Services.GetRequiredService<IMemoryCache>());
        memoryCache.Compact(1);
        using var allLibraryVersions = JsonDocument.Parse(await client.GetStringAsync("/flat/velopack/index.json", Cancellation));
        await Assert.That(allLibraryVersions.RootElement.GetProperty("versions").GetArrayLength()).IsEqualTo(2);
        using var allPluginVersions = JsonDocument.Parse(await client.GetStringAsync("/flat/example.plugin/index.json", Cancellation));
        await Assert.That(allPluginVersions.RootElement.GetProperty("versions").EnumerateArray()).HasSingleItem();
        using var pluginSearch = JsonDocument.Parse(await client.GetStringAsync(
            "/query?q=Example.Plugin&prerelease=true&semVerLevel=2.0.0", Cancellation));
        await Assert.That(pluginSearch.RootElement.GetProperty("data")[0].GetProperty("versions").EnumerateArray()).HasSingleItem();
        using var pluginAutocomplete = JsonDocument.Parse(await client.GetStringAsync(
            "/autocomplete?id=Example.Plugin&prerelease=true&semVerLevel=2.0.0", Cancellation));
        await Assert.That(pluginAutocomplete.RootElement.GetProperty("data").EnumerateArray()).HasSingleItem();
        _github.OpenPullRequests = [];
        memoryCache = await ProxyAssertions.ExactMemoryCache(app.Services.GetRequiredService<IMemoryCache>());
        memoryCache.Compact(1);
        await Assert.That((await client.GetAsync("/flat/velopack/index.json", Cancellation)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.GetAsync($"/flat/velopack/{version}/velopack.{version}.nupkg", Cancellation)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.GetAsync("/registration/velopack/index.json", Cancellation)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.GetAsync($"/registration/velopack/{version}.json", Cancellation)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        using var closedSearch = JsonDocument.Parse(await client.GetStringAsync("/query?prerelease=true&semVerLevel=2.0.0", Cancellation));
        await Assert.That(closedSearch.RootElement.GetProperty("data").EnumerateArray()).IsEmpty();
        using var closedAutocomplete = JsonDocument.Parse(await client.GetStringAsync(
            "/autocomplete?id=Velopack&prerelease=true&semVerLevel=2.0.0", Cancellation));
        await Assert.That(closedAutocomplete.RootElement.GetProperty("data").EnumerateArray()).IsEmpty();
        await app.StopAsync(Cancellation);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

}