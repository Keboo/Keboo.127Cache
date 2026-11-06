using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

using Keboo._127Cache;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NuGet.Packaging;

namespace Keboo._127Cache.Tests;

public sealed class OfflineFeedTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Directory.GetCurrentDirectory(), "TestResults", "offline-" + Guid.NewGuid().ToString("N"));
    private readonly OfflineFakeGitHub _github;
    private string CacheDirectory => Path.Combine(_directory, "cache");
    private string SnapshotPath => Path.Combine(CacheDirectory, "visible-runs.json");
    private string RunsPath => Path.Combine(CacheDirectory, "runs.json");

    public OfflineFeedTests()
    {
        Directory.CreateDirectory(_directory);
        _github = new OfflineFakeGitHub(_directory);
    }

    [Test]
    public async Task DiscoveryDoesNotBlockHostStartupOrHealth()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.DiscoveryBlock = block;
        var starting = StartAsync();
        try
        {
            var completed = await Task.WhenAny(starting, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != starting) block.TrySetResult();
            await Assert.That(completed == starting).IsTrue();
            await using var server = await starting;
            await WaitForDiscoveryAsync(1);
            using var health = await GetJsonAsync(server.Client, "/health");
            await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
            await Assert.That(health.RootElement.GetProperty("lastSuccessfulRefreshUtc").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
        finally
        {
            block.TrySetResult();
        }
    }

    [Test]
    public async Task EmptyCacheReturns503ForEveryFeedSurfaceAndRetries()
    {
        _github.DiscoveryError = new IOException("Synthetic network outage.");
        await using var server = await StartAsync();
        await WaitForDiscoveryAsync(1);
        var initialCalls = _github.DiscoveryCalls;
        foreach (var path in new[]
        {
            "/v3/index.json", "/builds", "/flat/alpha/index.json", "/registration/alpha/index.json",
            "/query?prerelease=false", "/query?prerelease=true&semVerLevel=2.0.0", "/autocomplete"
        })
        {
            using var response = await server.Client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
            await Assert.That(response.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded");
        }
        using (var head = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/flat/alpha/index.json")))
        {
            await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        }
        await Assert.That(_github.DiscoveryCalls).IsEqualTo(initialCalls + 8);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(health.RootElement.GetProperty("lastSuccessfulRefreshUtc").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("gh auth status");
    }

    [Test]
    public async Task CachedPackagesSurviveRestartAndRecoveryUpdatesVisibility()
    {
        var first = _github.AddBuild(100, 1, "Alpha");
        var second = _github.AddBuild(200, 2, "Beta");
        _github.Runs = [first, second];
        _github.OpenPullRequests = [new(1), new(2)];
        string alphaVersion;
        byte[] alphaBytes;
        DateTimeOffset lastSuccess;
        await using (var online = await StartAsync())
        {
            alphaVersion = await SingleVersionAsync(online.Client, "alpha");
            alphaBytes = await online.Client.GetByteArrayAsync(PackageUrl("alpha", alphaVersion));
            await SingleVersionAsync(online.Client, "beta");
            using var health = await GetJsonAsync(online.Client, "/health");
            await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
            lastSuccess = health.RootElement.GetProperty("lastSuccessfulRefreshUtc").GetDateTimeOffset();
        }

        var historicalBeforeFailure = await File.ReadAllTextAsync(RunsPath);
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using (var offline = await StartAsync())
        {
            await WaitForDiscoveryAsync(2);
            var calls = _github.DiscoveryCalls;
            using (var index = await offline.Client.GetAsync("/v3/index.json"))
            {
                await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(index.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded");
            }
            await Assert.That(await SingleVersionAsync(offline.Client, "alpha")).IsEqualTo(alphaVersion);
            var restored = await offline.Client.GetByteArrayAsync(PackageUrl("alpha", alphaVersion));
            await Assert.That(restored.SequenceEqual(alphaBytes)).IsTrue();
            using var registration = await GetJsonAsync(offline.Client, "/registration/alpha/index.json");
            await Assert.That(registration.RootElement.GetProperty("count").GetInt32()).IsEqualTo(1);
            using var search = await GetJsonAsync(offline.Client, "/query?prerelease=true&semVerLevel=2.0.0");
            await Assert.That(search.RootElement.GetProperty("totalHits").GetInt32()).IsEqualTo(2);
            await Assert.That(_github.DiscoveryCalls > calls).IsTrue();
            await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
            using (var health = await GetJsonAsync(offline.Client, "/health"))
            {
                await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
                await Assert.That(health.RootElement.GetProperty("lastSuccessfulRefreshUtc").GetDateTimeOffset()).IsEqualTo(lastSuccess);
                await Assert.That(health.RootElement.GetProperty("availableCachedBuilds").GetInt32()).IsEqualTo(2);
                await Assert.That(health.RootElement.GetProperty("unavailableBuilds").GetInt32()).IsEqualTo(0);
            }
            await Assert.That(await File.ReadAllTextAsync(RunsPath)).IsEqualTo(historicalBeforeFailure);

            _github.DiscoveryError = null;
            _github.OpenPullRequests = [new(2)];
            await RetryUntilHealthyAsync(offline.Client);
            await SingleVersionAsync(offline.Client, "beta");
            using (var health = await GetJsonAsync(offline.Client, "/health"))
                await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
            using var closed = await offline.Client.GetAsync("/flat/alpha/index.json");
            await Assert.That(closed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(RunsPath));
            await Assert.That(saved.RootElement.GetProperty("runs").GetArrayLength()).IsEqualTo(2);
        }

        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        await using var secondOffline = await StartAsync();
        using var noLongerVisible = await secondOffline.Client.GetAsync("/flat/alpha/index.json");
        await Assert.That(noLongerVisible.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await SingleVersionAsync(secondOffline.Client, "beta");
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
    }

    [Test]
    public async Task ReleasedMainDiskCacheWorksOfflineWithUncertainMembership()
    {
        // Generated by PackageCatalog.GetPackagesAsync at released-main 76253625ee24b80301555e4663c4fb6b99bd6317,
        // using that revision's FakeGitHub and two sequential successful runs of PR 755.
        // Regenerate with scripts/New-ReleasedCacheFixture.ps1.
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleasedMain-7625362.zip");
        ZipFile.ExtractToDirectory(fixture, CacheDirectory);
        var runsBefore = await File.ReadAllBytesAsync(RunsPath);
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;

        await using var server = await StartAsync();
        using var index = await server.Client.GetAsync("/flat/velopack/index.json");
        await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(index.Headers.GetValues("X-127Cache-Status").Single()).Contains("degraded");
        await Assert.That(index.Headers.GetValues("X-127Cache-Membership").Single()).IsEqualTo("unknown");
        using var json = JsonDocument.Parse(await index.Content.ReadAsStringAsync());
        var versions = json.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()!).ToArray();
        await Assert.That(versions).IsEquivalentTo(new[]
        {
            "1.3.67-pr.755.run.99.attempt.1.artifact.3",
            "1.3.67-pr.755.run.100.attempt.1.artifact.2"
        });
        foreach (var version in versions)
        {
            using var response = await server.Client.GetAsync(PackageUrl("velopack", version));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var package = new PackageArchiveReader(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            await Assert.That(package.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
        }
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(health.RootElement.GetProperty("membership").GetString()).IsEqualTo("unknown");
        await Assert.That(health.RootElement.GetProperty("lastSuccessfulRefreshUtc").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("membership");
        await Assert.That((await File.ReadAllBytesAsync(RunsPath)).SequenceEqual(runsBefore)).IsTrue();
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();
        await Assert.That(_github.DownloadCalls).IsEqualTo(0);
        using (var search = await server.Client.GetAsync("/query?prerelease=true&semVerLevel=2.0.0"))
            await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        _github.DiscoveryError = null;
        _github.OpenPullRequests = [];
        await RetryUntilHealthyAsync(server.Client);
        using var closed = await server.Client.GetAsync("/flat/velopack/index.json");
        await Assert.That(closed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(File.Exists(SnapshotPath)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialVersionsReturnProblemsForIndexesButServeUnrelatedCachedPackages(bool corrupt)
    {
        var old = _github.AddBuild(100, 1, "Alpha");
        var latest = _github.AddBuild(101, 1, "Alpha", "Beta");
        _github.Runs = [latest, old];
        _github.OpenPullRequests = [new(1)];
        string oldVersion;
        string latestVersion;
        await using (var online = await StartAsync(runIds: [100]))
        {
            var versions = await VersionsAsync(online.Client, "alpha");
            await Assert.That(versions.Length).IsEqualTo(2);
            oldVersion = versions.Single(x => x.Contains(".run.100.", StringComparison.Ordinal));
            latestVersion = versions.Single(x => x.Contains(".run.101.", StringComparison.Ordinal));
        }
        var oldPackage = CachedPackagePath(100, 1, "alpha", oldVersion);
        if (corrupt) await File.WriteAllTextAsync(oldPackage, "corrupt-package");
        else File.Delete(oldPackage);
        File.Delete(Path.Combine(CacheDirectory, "artifacts", _github.PackagesArtifact(100).Id + ".zip"));
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync(runIds: [100]);
        foreach (var path in new[] { "/flat/alpha/index.json", "/registration/alpha/index.json" })
        {
            using var response = await offline.Client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(problem.RootElement.GetProperty("status").GetInt32()).IsEqualTo(503);
        }
        using (var package = await offline.Client.GetAsync(PackageUrl("alpha", latestVersion)))
            await Assert.That(package.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var other = await offline.Client.GetAsync("/flat/beta/index.json"))
        {
            await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(other.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        }
        using var aggregate = await offline.Client.GetAsync("/query?prerelease=true&semVerLevel=2.0.0");
        await Assert.That(aggregate.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("unavailablePackages").GetInt32()).IsEqualTo(1);
        if (corrupt) await Assert.That(await File.ReadAllTextAsync(oldPackage)).IsEqualTo("corrupt-package");
    }

    [Test]
    public async Task OnlinePartialVersionIndexNeverClaimsACompleteSet()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(101, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        await using var server = await StartAsync(runIds: [100]);
        var versions = await VersionsAsync(server.Client, "alpha");
        var oldVersion = versions.Single(x => x.Contains(".run.100.", StringComparison.Ordinal));
        var latestVersion = versions.Single(x => x.Contains(".run.101.", StringComparison.Ordinal));
        var oldPackage = CachedPackagePath(100, 1, "alpha", oldVersion);
        await File.WriteAllTextAsync(oldPackage, "corrupt-package");

        foreach (var path in new[] { "/flat/alpha/index.json", "/registration/alpha/index.json" })
        {
            using var index = await server.Client.GetAsync(path);
            await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        }
        using var healthy = await server.Client.GetAsync(PackageUrl("alpha", latestVersion));
        await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(await File.ReadAllTextAsync(oldPackage)).IsEqualTo("corrupt-package");
    }

    [Test]
    public async Task LegacyCatalogAloneCannotEstablishWorkflowIdentity()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleasedMain-7625362.zip");
        ZipFile.ExtractToDirectory(fixture, CacheDirectory);
        File.Delete(RunsPath);
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        await using var server = await StartAsync();
        using var index = await server.Client.GetAsync("/flat/velopack/index.json");
        await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        await Assert.That(File.Exists(Path.Combine(CacheDirectory, "catalog.json"))).IsTrue();
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();
    }

    [Test]
    public async Task CatalogOnlyCannotPromoteUnverifiedOldRunAfterOnlineDiscovery()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleasedMain-7625362.zip");
        ZipFile.ExtractToDirectory(fixture, CacheDirectory);
        var saved = JsonSerializer.Deserialize<RunArtifactsFile>(await File.ReadAllTextAsync(RunsPath), JsonSettings.Options)!;
        var latest = saved.Runs.Single(run => run.RunId == 100);
        File.Delete(RunsPath);
        _github.AddArtifactMetadata(latest.RunId, latest.VersionArtifact, latest.Packages);
        _github.OpenPullRequests = [new(755)];
        _github.Runs = [new(100, 1, "head", "completed", "success", "pull_request", [new(755)])];
        _github.FailDownloads = true;

        await using var server = await StartAsync();
        await RetryUntilMembershipAsync(server.Client, "current");
        using var index = await server.Client.GetAsync("/flat/velopack/index.json");
        await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();
    }

    [Test]
    public async Task CatalogOnlyCachedRunsRecoverWhenLiveWorkflowCorroboratesThem()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleasedMain-7625362.zip");
        ZipFile.ExtractToDirectory(fixture, CacheDirectory);
        var saved = JsonSerializer.Deserialize<RunArtifactsFile>(await File.ReadAllTextAsync(RunsPath), JsonSettings.Options)!;
        File.Delete(RunsPath);
        foreach (var run in saved.Runs)
            _github.AddArtifactMetadata(run.RunId, run.VersionArtifact, run.Packages);
        _github.OpenPullRequests = [new(755)];
        _github.Runs = [
            new(100, 1, "head", "completed", "success", "pull_request", [new(755)]),
            new(99, 1, "old", "completed", "success", "pull_request", [new(755)])
        ];
        _github.FailDownloads = true;

        await using var server = await StartAsync();
        await RetryUntilHealthyAsync(server.Client, validateContent: true);
        await Assert.That(await VersionsAsync(server.Client, "velopack")).IsEquivalentTo(new[]
        {
            "1.3.67-pr.755.run.99.attempt.1.artifact.3",
            "1.3.67-pr.755.run.100.attempt.1.artifact.2"
        });
        await Assert.That(_github.ArtifactQueries).IsEquivalentTo(new long[] { 99, 100 });
        await Assert.That(File.Exists(SnapshotPath)).IsTrue();
        await Assert.That(_github.DownloadCalls).IsEqualTo(0);
    }

    [Test]
    public async Task LegacyDisagreementBetweenCatalogAndRunsCannotProduceAPartialVersionIndex()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleasedMain-7625362.zip");
        ZipFile.ExtractToDirectory(fixture, CacheDirectory);
        var runs = JsonSerializer.Deserialize<RunArtifactsFile>(await File.ReadAllTextAsync(RunsPath), JsonSettings.Options)!;
        var old = runs.Runs.Single(run => run.RunId == 99);
        runs.Runs.RemoveAll(run => run.RunId == 99);
        await JsonSettings.WriteAtomicAsync(RunsPath, runs, CancellationToken.None);
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var server = await StartAsync();
        using (var index = await server.Client.GetAsync("/flat/velopack/index.json"))
        {
            await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        }
        const string latest = "1.3.67-pr.755.run.100.attempt.1.artifact.2";
        using (var package = await server.Client.GetAsync(PackageUrl("velopack", latest)))
            await Assert.That(package.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("membership").GetString()).IsEqualTo("unknown");
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("metadata");

        _github.DiscoveryError = null;
        _github.OpenPullRequests = [new(755)];
        _github.Runs = [new(100, 1, "head", "completed", "success", "pull_request", [new(755)])];
        await RetryUntilMembershipAsync(server.Client, "current");
        using var refreshedIndex = await server.Client.GetAsync("/flat/velopack/index.json");
        await Assert.That(refreshedIndex.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();

        _github.AddArtifactMetadata(old.RunId, old.VersionArtifact, old.Packages);
        _github.Runs = [
            new(100, 1, "head", "completed", "success", "pull_request", [new(755)]),
            new(99, 1, "old", "completed", "success", "pull_request", [new(755)])
        ];
        await RetryUntilHealthyAsync(server.Client, validateContent: true);
        await Assert.That(await VersionsAsync(server.Client, "velopack")).IsEquivalentTo(new[]
        {
            "1.3.67-pr.755.run.99.attempt.1.artifact.3",
            "1.3.67-pr.755.run.100.attempt.1.artifact.2"
        });
        await Assert.That(_github.ArtifactQueries).Contains(99L);
        await Assert.That(File.Exists(SnapshotPath)).IsTrue();
    }

    [Test]
    public async Task HungConcurrentDiscoveryDoesNotBlockUsableCachedFeedAndIsRetried()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        await WaitForHealthErrorAsync(offline.Client, "gh auth status");

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.DiscoveryBlock = block;
        var before = _github.DiscoveryCalls;
        var requests = new[]
        {
            "/v3/index.json", "/flat/alpha/index.json", "/registration/alpha/index.json", PackageUrl("alpha", version)
        }.Select(path => offline.Client.GetAsync(path)).ToArray();
        try
        {
            await WaitForDiscoveryAsync(before + 1);
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var response in responses)
            {
                using (response)
                {
                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(response.Headers.GetValues("X-127Cache-Status").Single()).Contains("degraded");
                }
            }
            await Assert.That(_github.DiscoveryCalls).IsEqualTo(before + 1);
        }
        finally
        {
            block.TrySetResult();
        }
        _github.DiscoveryBlock = null;
        var after = _github.DiscoveryCalls;
        await WaitForHealthErrorAsync(offline.Client, "gh auth status");
        var retryDeadline = DateTime.UtcNow.AddSeconds(10);
        while (_github.DiscoveryCalls == after && DateTime.UtcNow < retryDeadline)
        {
            using var retry = await offline.Client.GetAsync("/flat/alpha/index.json");
            await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Task.Delay(20);
        }
        await Assert.That(_github.DiscoveryCalls > after).IsTrue();
        _github.DiscoveryError = null;
        _github.FailDownloads = false;
        await RetryUntilHealthyAsync(offline.Client, validateContent: true);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("expired")]
    public async Task UnpinnedMissingOrExpiredBuildDoesNotHideHealthyBuild(string failure)
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(200, 2, "Beta")];
        _github.OpenPullRequests = [new(1), new(2)];
        if (failure == "missing") _github.MissingVersionArtifactForRun = 200;
        else _github.ExpiredRuns.Add(200);
        await using var server = await StartAsync();
        using var alpha = await server.Client.GetAsync("/flat/alpha/index.json");
        await Assert.That(alpha.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(alpha.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        using var alphaIndex = JsonDocument.Parse(await alpha.Content.ReadAsStringAsync());
        var version = alphaIndex.RootElement.GetProperty("versions")[0].GetString()!;
        using var package = await server.Client.GetAsync(PackageUrl("alpha", version));
        await Assert.That(package.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var search = await server.Client.GetAsync("/query?prerelease=true&semVerLevel=2.0.0");
        await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains(failure);
    }

    [Test]
    public async Task CorruptUnpinnedPackageDoesNotHideAnUnrelatedCachedBuild()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(200, 2, "Beta")];
        _github.OpenPullRequests = [new(1), new(2)];
        string alphaVersion;
        string betaVersion;
        await using (var online = await StartAsync())
        {
            alphaVersion = await SingleVersionAsync(online.Client, "alpha");
            betaVersion = await SingleVersionAsync(online.Client, "beta");
        }
        var beta = CachedPackagePath(200, 2, "beta", betaVersion);
        await File.WriteAllTextAsync(beta, "corrupt");
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        using (var alpha = await offline.Client.GetAsync("/flat/alpha/index.json"))
        {
            await Assert.That(alpha.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(alpha.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        }
        using (var package = await offline.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
            await Assert.That(package.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var missing = await offline.Client.GetAsync("/flat/beta/index.json"))
        {
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(missing.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        }
        await Assert.That(await File.ReadAllTextAsync(beta)).IsEqualTo("corrupt");
    }

    [Test]
    public async Task CorruptUnpinnedVersionDoesNotBlockHealthyBuildAndCanRecover()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(200, 2, "Beta")];
        _github.OpenPullRequests = [new(1), new(2)];
        var damagedVersion = _github.ArtifactFile(_github.VersionArtifact(200).Id);
        var original = await File.ReadAllBytesAsync(damagedVersion);
        await File.AppendAllTextAsync(damagedVersion, "invalid");

        await using var server = await StartAsync();
        const string alphaVersion = "1.2.3-pr.1.run.100.attempt.1.artifact.1002";
        using (var alpha = await server.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
        {
            await Assert.That(alpha.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(alpha.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        }
        using (var index = await server.Client.GetAsync("/flat/alpha/index.json"))
        {
            await Assert.That(index.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        }
        using (var search = await server.Client.GetAsync("/query?prerelease=true&semVerLevel=2.0.0"))
            await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var health = await GetJsonAsync(server.Client, "/health"))
            await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");

        await File.WriteAllBytesAsync(damagedVersion, original);
        await RetryUntilHealthyAsync(server.Client, validateContent: true);
        await Assert.That(await SingleVersionAsync(server.Client, "beta"))
            .IsEqualTo("1.2.3-pr.2.run.200.attempt.1.artifact.2002");
        await Assert.That(await SingleVersionAsync(server.Client, "alpha")).IsEqualTo(alphaVersion);
    }

    [Test]
    public async Task DigestlessOfflineReconstructionRejectsSidecarAndZipTamperingAndRecovers()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(200, 2, "Beta")];
        _github.UseDigestlessArtifacts(100);
        _github.UseDigestlessArtifacts(200);
        _github.OpenPullRequests = [new(1), new(2)];
        string alphaVersion;
        string betaVersion;
        await using (var online = await StartAsync())
        {
            alphaVersion = await SingleVersionAsync(online.Client, "alpha");
            betaVersion = await SingleVersionAsync(online.Client, "beta");
        }
        var raw = Path.Combine(CacheDirectory, "artifacts", _github.PackagesArtifact(100).Id + ".zip");
        var sidecar = raw + ".sha256";
        var originalBytes = await File.ReadAllBytesAsync(raw);
        var originalHash = await File.ReadAllTextAsync(sidecar);
        var packageDirectory = Path.GetDirectoryName(CachedPackagePath(100, 1, "alpha", alphaVersion))!;
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        File.Delete(sidecar);
        await using var offline = await StartAsync();
        using (var rewritten = await offline.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
            await Assert.That(rewritten.StatusCode).IsEqualTo(HttpStatusCode.OK);
        Directory.Delete(packageDirectory, recursive: true);
        using (var missingRecord = await offline.Client.GetAsync("/flat/alpha/index.json"))
            await Assert.That(missingRecord.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(File.Exists(sidecar)).IsFalse();
        await Assert.That(Directory.Exists(packageDirectory)).IsFalse();

        await File.WriteAllTextAsync(sidecar, "tampered-record");
        using (var rejected = await offline.Client.GetAsync("/flat/alpha/index.json"))
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var healthy = await offline.Client.GetAsync(PackageUrl("beta", betaVersion)))
            await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await File.ReadAllTextAsync(sidecar)).IsEqualTo("tampered-record");
        await Assert.That(Directory.Exists(packageDirectory)).IsFalse();

        await File.WriteAllTextAsync(sidecar, originalHash);
        using (var repaired = await offline.Client.GetAsync("/flat/alpha/index.json"))
            await Assert.That(repaired.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await File.WriteAllBytesAsync(raw, [.. originalBytes, 42]);
        using (var rewritten = await offline.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
            await Assert.That(rewritten.StatusCode).IsEqualTo(HttpStatusCode.OK);
        Directory.Delete(packageDirectory, recursive: true);
        using (var rejected = await offline.Client.GetAsync("/flat/alpha/index.json"))
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await File.ReadAllTextAsync(sidecar)).IsEqualTo(originalHash);
        await Assert.That(Directory.Exists(packageDirectory)).IsFalse();
        await File.WriteAllBytesAsync(raw, originalBytes);
        using (var repaired = await offline.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
            await Assert.That(repaired.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
    }

    [Test]
    public async Task DotnetRestoreResolvesCachedPackageWhileDiscoveryIsOffline()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        var consumer = Path.Combine(_directory, "consumer");
        Directory.CreateDirectory(consumer);
        var project = Path.Combine(consumer, "consumer.csproj");
        var config = Path.Combine(consumer, "NuGet.config");
        var packages = Path.Combine(consumer, "packages");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageReference Include="Alpha" Version="{{version}}" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(config, $$"""
            <configuration><packageSources><clear />
              <add key="offline" value="{{offline.Client.BaseAddress}}v3/index.json" allowInsecureConnections="true" />
            </packageSources></configuration>
            """);
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = Directory.GetParent(runtime)!.Parent!.Parent!.FullName;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            WorkingDirectory = consumer,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "restore", project, "--configfile", config, "--packages", packages, "--no-cache", "--verbosity", "quiet" })
            start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = consumer;
        start.Environment["TMP"] = consumer;
        start.Environment["TMPDIR"] = consumer;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet restore did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Offline dotnet restore failed: " + await output + await error);
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(consumer, "obj", "project.assets.json")));
        await Assert.That(assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Any(property => property.Name.Equals("Alpha/" + version, StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(packages, "alpha", version, "lib", "net8.0", "payload.txt"))).IsTrue();
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
    }

    [Test]
    public async Task PreviouslyServedRunsRemainVisibleWhileTheirPullRequestIsOpen()
    {
        var old = _github.AddBuild(100, 1, "Alpha");
        var latest = _github.AddBuild(101, 1, "Alpha");
        _github.OpenPullRequests = [new(1)];
        _github.Runs = [old];
        string oldVersion;
        string latestVersion;
        await using (var online = await StartAsync())
        {
            oldVersion = await SingleVersionAsync(online.Client, "alpha");
            _github.Runs = [latest];
            ExpireDiscovery(online);
            await RetryUntilHealthyAsync(online.Client);
            var versions = await VersionsAsync(online.Client, "alpha");
            await Assert.That(versions.Length).IsEqualTo(2);
            latestVersion = versions.Single(x => x.Contains(".run.101.", StringComparison.Ordinal));
            await Assert.That(versions).Contains(oldVersion);
            await Assert.That(latestVersion != oldVersion).IsTrue();
            using var history = JsonDocument.Parse(await File.ReadAllTextAsync(RunsPath));
            await Assert.That(history.RootElement.GetProperty("runs").GetArrayLength()).IsEqualTo(2);
            using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(SnapshotPath));
            await Assert.That(snapshot.RootElement.GetProperty("runs").GetArrayLength()).IsEqualTo(2);
        }

        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        await Assert.That(await VersionsAsync(offline.Client, "alpha")).IsEquivalentTo(new[] { oldVersion, latestVersion });
        using var oldPackage = await offline.Client.GetAsync(PackageUrl("alpha", oldVersion));
        await Assert.That(oldPackage.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var historyAfter = JsonDocument.Parse(await File.ReadAllTextAsync(RunsPath));
        await Assert.That(historyAfter.RootElement.GetProperty("runs").GetArrayLength()).IsEqualTo(2);
        _github.DiscoveryError = null;
        _github.OpenPullRequests = [];
        await RetryUntilHealthyAsync(offline.Client);
        using var closed = await offline.Client.GetAsync(PackageUrl("alpha", oldVersion));
        await Assert.That(closed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MatchingSnapshotPreservesOlderRunsWhenRunHistoryIsMissing()
    {
        var old = _github.AddBuild(100, 1, "Alpha");
        var latest = _github.AddBuild(101, 1, "Alpha");
        _github.OpenPullRequests = [new(1)];
        _github.Runs = [old];
        string[] expected;
        await using (var online = await StartAsync())
        {
            await SingleVersionAsync(online.Client, "alpha");
            _github.Runs = [latest];
            ExpireDiscovery(online);
            await RetryUntilHealthyAsync(online.Client);
            expected = await VersionsAsync(online.Client, "alpha");
            await Assert.That(expected.Length).IsEqualTo(2);
        }

        File.Delete(RunsPath);
        _github.Runs = [latest];
        await using var restarted = await StartAsync();
        await RetryUntilHealthyAsync(restarted.Client, validateContent: true);
        await Assert.That(await VersionsAsync(restarted.Client, "alpha")).IsEquivalentTo(expected);
        using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(SnapshotPath));
        await Assert.That(snapshot.RootElement.GetProperty("runs").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task PartialDiscoveryFailureDoesNotReplaceSnapshotOrHistoricalMetadata()
    {
        var first = _github.AddBuild(100, 1, "Alpha");
        var second = _github.AddBuild(200, 2, "Beta");
        var third = _github.AddBuild(300, 3, "Gamma");
        _github.OpenPullRequests = [new(1)];
        _github.Runs = [first];
        await using var server = await StartAsync();
        await SingleVersionAsync(server.Client, "alpha");
        var snapshot = await File.ReadAllBytesAsync(SnapshotPath);
        var history = await File.ReadAllBytesAsync(RunsPath);
        _github.OpenPullRequests = [new(1), new(2), new(3)];
        _github.Runs = [first, second, third];
        _github.FailArtifactsForRun = 300;
        ExpireDiscovery(server);
        await SingleVersionAsync(server.Client, "alpha");
        await WaitForHealthErrorAsync(server.Client, "network connection");
        await Assert.That((await File.ReadAllBytesAsync(SnapshotPath)).SequenceEqual(snapshot)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(RunsPath)).SequenceEqual(history)).IsTrue();
        using var beta = await server.Client.GetAsync("/flat/beta/index.json");
        await Assert.That(beta.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
    }

    [Test]
    public async Task EmptySuccessfulSnapshotDoesNotBecomeAnOfflineEmptyFeed()
    {
        _github.OpenPullRequests = [];
        await using var server = await StartAsync();
        using (var healthy = await GetJsonAsync(server.Client, "/query?prerelease=true&semVerLevel=2.0.0"))
            await Assert.That(healthy.RootElement.GetProperty("totalHits").GetInt32()).IsEqualTo(0);
        using var before = await GetJsonAsync(server.Client, "/health");
        var timestamp = before.RootElement.GetProperty("lastSuccessfulRefreshUtc").GetDateTimeOffset();
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        ExpireDiscovery(server);
        using var response = await server.Client.GetAsync("/v3/index.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(health.RootElement.GetProperty("lastSuccessfulRefreshUtc").GetDateTimeOffset()).IsEqualTo(timestamp);
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("contained no builds");
    }

    [Test]
    public async Task HistoricalCacheWithoutSnapshotHonorsSelectionAndReportsUnknownMembership()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        var history = await File.ReadAllBytesAsync(RunsPath);
        var packagePath = CachedPackagePath(100, 1, "alpha", version);
        File.Delete(SnapshotPath);
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        await using (var offline = await StartAsync([1]))
        {
            using var response = await offline.Client.GetAsync("/flat/alpha/index.json");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.GetValues("X-127Cache-Membership").Single()).IsEqualTo("unknown");
            using var search = await offline.Client.GetAsync("/query?prerelease=true&semVerLevel=2.0.0");
            await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
        await using (var excluded = await StartAsync([2]))
        {
            using var response = await excluded.Client.GetAsync("/v3/index.json");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
        await Assert.That((await File.ReadAllBytesAsync(RunsPath)).SequenceEqual(history)).IsTrue();
        await Assert.That(File.Exists(packagePath)).IsTrue();
    }

    [Test]
    public async Task SnapshotFromDifferentSelectionIsNotServed()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(200, 2, "Beta")];
        _github.OpenPullRequests = [new(1), new(2)];
        await using (var online = await StartAsync([1]))
            await SingleVersionAsync(online.Client, "alpha");
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        await using var offline = await StartAsync([2]);
        using var response = await offline.Client.GetAsync("/v3/index.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("different source or discovery configuration");
    }

    [Test]
    public async Task MetadataOnlySnapshotIsNotAnAvailableOfflineFeed()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        await using (var online = await StartAsync())
            await online.App.Services.GetRequiredService<PackageCatalog>().GetRunArtifactsAsync(CancellationToken.None);
        await Assert.That(_github.DownloadCalls).IsEqualTo(0);
        var savedSnapshot = await File.ReadAllBytesAsync(SnapshotPath);
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        using var response = await offline.Client.GetAsync("/v3/index.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("snapshotBuilds").GetInt32()).IsEqualTo(1);
        await Assert.That(health.RootElement.GetProperty("unavailableBuilds").GetInt32()).IsEqualTo(1);
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("No usable cached packages");
        await Assert.That((await File.ReadAllBytesAsync(SnapshotPath)).SequenceEqual(savedSnapshot)).IsTrue();
        await Assert.That(_github.DownloadCalls).IsEqualTo(0);
    }

    [Test]
    public async Task UncachedBuildInSnapshotDoesNotHideAnotherCachedBuild()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha"), _github.AddBuild(200, 2, "Beta")];
        _github.OpenPullRequests = [new(1), new(2)];
        string alphaVersion;
        string betaVersion;
        await using (var online = await StartAsync())
        {
            alphaVersion = await SingleVersionAsync(online.Client, "alpha");
            betaVersion = await SingleVersionAsync(online.Client, "beta");
        }
        var betaDirectory = Path.GetDirectoryName(CachedPackagePath(200, 2, "beta", betaVersion))!;
        Directory.Delete(betaDirectory, recursive: true);
        File.Delete(Path.Combine(CacheDirectory, "artifacts", _github.PackagesArtifact(200).Id + ".zip"));
        var history = await File.ReadAllBytesAsync(RunsPath);
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        using (var alphaIndex = await offline.Client.GetAsync("/flat/alpha/index.json"))
        {
            await Assert.That(alphaIndex.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(alphaIndex.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        }
        using (var alpha = await offline.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
            await Assert.That(alpha.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var beta = await offline.Client.GetAsync("/flat/beta/index.json");
        await Assert.That(beta.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("snapshotBuilds").GetInt32()).IsEqualTo(2);
        await Assert.That(health.RootElement.GetProperty("availableCachedBuilds").GetInt32()).IsEqualTo(1);
        await Assert.That(health.RootElement.GetProperty("unavailableBuilds").GetInt32()).IsEqualTo(1);
        await Assert.That((await File.ReadAllBytesAsync(RunsPath)).SequenceEqual(history)).IsTrue();
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
    }

    [Test]
    public async Task DownloadFailureAfterCachedDiscoveryFallsBackAndRetries()
    {
        var first = _github.AddBuild(100, 1, "Alpha");
        var second = _github.AddBuild(200, 2, "Beta");
        _github.Runs = [first, second];
        _github.OpenPullRequests = [new(1)];
        await using var server = await StartAsync();
        var alphaVersion = await SingleVersionAsync(server.Client, "alpha");
        _github.OpenPullRequests = [new(1), new(2)];
        ExpireDiscovery(server);
        await server.App.Services.GetRequiredService<PackageCatalog>().GetRunArtifactsAsync(CancellationToken.None);
        await RetryUntilHealthyAsync(server.Client);
        var calls = _github.DiscoveryCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;

        using (var alpha = await server.Client.GetAsync("/flat/alpha/index.json"))
        {
            await Assert.That(alpha.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(alpha.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        }
        using (var health = await GetJsonAsync(server.Client, "/health"))
        {
            await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
            await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("artifact is unavailable");
            await Assert.That(health.RootElement.GetProperty("unavailableBuilds").GetInt32()).IsEqualTo(1);
        }
        using (var alphaPackage = await server.Client.GetAsync(PackageUrl("alpha", alphaVersion)))
            await Assert.That(alphaPackage.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var beta = await server.Client.GetAsync("/flat/beta/index.json"))
            await Assert.That(beta.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(_github.DiscoveryCalls > calls).IsTrue();

        _github.DiscoveryError = null;
        _github.FailDownloads = false;
        await RetryUntilHealthyAsync(server.Client, validateContent: true);
        await SingleVersionAsync(server.Client, "beta");
        await Assert.That(await SingleVersionAsync(server.Client, "alpha")).IsEqualTo(alphaVersion);
        using var recovered = await GetJsonAsync(server.Client, "/health");
        await Assert.That(recovered.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
    }

    [Test]
    public async Task SearchReturns503WhenContentFailureMakesAnOnlineSnapshotPartial()
    {
        var first = _github.AddBuild(100, 1, "Alpha");
        var second = _github.AddBuild(200, 2, "Beta");
        _github.Runs = [first, second];
        _github.OpenPullRequests = [new(1)];
        await using var server = await StartAsync();
        var alphaVersion = await SingleVersionAsync(server.Client, "alpha");
        _github.OpenPullRequests = [new(1), new(2)];
        ExpireDiscovery(server);
        await server.App.Services.GetRequiredService<PackageCatalog>().GetRunArtifactsAsync(CancellationToken.None);
        await RetryUntilHealthyAsync(server.Client);
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;

        using var search = await server.Client.GetAsync("/query?q=Beta&prerelease=true&semVerLevel=2.0.0");
        await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(search.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        using var alpha = await server.Client.GetAsync(PackageUrl("alpha", alphaVersion));
        await Assert.That(alpha.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task CorruptSnapshotIsNotReplacedOrReconstructedFromHistoryOffline()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        await using (var online = await StartAsync())
            await SingleVersionAsync(online.Client, "alpha");
        var history = await File.ReadAllBytesAsync(RunsPath);
        await File.WriteAllTextAsync(SnapshotPath, "{truncated");
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        await using var offline = await StartAsync();
        using var response = await offline.Client.GetAsync("/v3/index.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("snapshot cannot be read");
        await Assert.That(await File.ReadAllTextAsync(SnapshotPath)).IsEqualTo("{truncated");
        await Assert.That((await File.ReadAllBytesAsync(RunsPath)).SequenceEqual(history)).IsTrue();
    }

    [Test]
    public async Task CorruptMetadataIsPreservedWhileCachedPackagesRemainAvailable()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        var catalogPath = Path.Combine(CacheDirectory, "catalog.json");
        var catalog = await File.ReadAllBytesAsync(catalogPath);
        var runs = await File.ReadAllBytesAsync(RunsPath);
        const string incompleteCatalog = """{"repository":"fixture/project","builds":[{"runId":100}]}""";
        await File.WriteAllTextAsync(catalogPath, incompleteCatalog);
        await File.WriteAllTextAsync(RunsPath, "{broken-runs");
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        await Assert.That(await SingleVersionAsync(offline.Client, "alpha")).IsEqualTo(version);
        using (var health = await GetJsonAsync(offline.Client, "/health"))
            await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("original files were preserved");
        await Assert.That(await File.ReadAllTextAsync(catalogPath)).IsEqualTo(incompleteCatalog);
        await Assert.That(await File.ReadAllTextAsync(RunsPath)).IsEqualTo("{broken-runs");

        await File.WriteAllBytesAsync(catalogPath, catalog);
        await File.WriteAllBytesAsync(RunsPath, runs);
        _github.DiscoveryError = null;
        await RetryUntilHealthyAsync(offline.Client);
        await Assert.That(await SingleVersionAsync(offline.Client, "alpha")).IsEqualTo(version);
        using var recovered = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(recovered.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
    }

    [Test]
    public async Task PartialCacheServesPresentPackagesAndRecoversMissingOnes()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha", "Shared")];
        _github.OpenPullRequests = [new(1)];
        string version;
        byte[] alphaBytes;
        await using (var online = await StartAsync())
        {
            version = await SingleVersionAsync(online.Client, "alpha");
            alphaBytes = await online.Client.GetByteArrayAsync(PackageUrl("alpha", version));
        }
        var sharedPath = CachedPackagePath(100, 1, "shared", version);
        var inventory = Path.Combine(Path.GetDirectoryName(sharedPath)!, "packages.json");
        var inventoryBefore = await File.ReadAllBytesAsync(inventory);
        File.Delete(sharedPath);
        File.Delete(Path.Combine(CacheDirectory, "artifacts", _github.PackagesArtifact(100).Id + ".zip"));
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        using (var index = await offline.Client.GetAsync("/v3/index.json"))
            await Assert.That(index.Headers.GetValues("X-127Cache-Status").Single()).IsEqualTo("degraded-partial");
        await Assert.That((await offline.Client.GetByteArrayAsync(PackageUrl("alpha", version))).SequenceEqual(alphaBytes)).IsTrue();
        using (var absent = await offline.Client.GetAsync("/flat/shared/index.json"))
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var search = await offline.Client.GetAsync("/query?prerelease=true&semVerLevel=2.0.0"))
            await Assert.That(search.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var builds = await offline.Client.GetAsync("/builds"))
            await Assert.That(builds.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var health = await GetJsonAsync(offline.Client, "/health"))
        {
            await Assert.That(health.RootElement.GetProperty("unavailablePackages").GetInt32()).IsEqualTo(1);
            await Assert.That(health.RootElement.GetProperty("availableCachedBuilds").GetInt32()).IsEqualTo(1);
        }
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
        await Assert.That((await File.ReadAllBytesAsync(inventory)).SequenceEqual(inventoryBefore)).IsTrue();

        _github.DiscoveryError = null;
        _github.FailDownloads = false;
        await RetryUntilHealthyAsync(offline.Client, validateContent: true);
        await Assert.That(await SingleVersionAsync(offline.Client, "shared")).IsEqualTo(version);
        await Assert.That(File.Exists(sharedPath)).IsTrue();
        await Assert.That((await offline.Client.GetByteArrayAsync(PackageUrl("alpha", version))).SequenceEqual(alphaBytes)).IsTrue();
        using var recovered = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(recovered.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
    }

    [Test]
    public async Task OfflineRepairUsesCachedRawArtifactWithoutOverwritingPresentFiles()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha", "Shared")];
        _github.OpenPullRequests = [new(1)];
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        var alphaPath = CachedPackagePath(100, 1, "alpha", version);
        var sharedPath = CachedPackagePath(100, 1, "shared", version);
        var alphaBefore = await File.ReadAllBytesAsync(alphaPath);
        File.Delete(sharedPath);
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        await Assert.That(await SingleVersionAsync(offline.Client, "shared")).IsEqualTo(version);
        await Assert.That(File.Exists(sharedPath)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(alphaPath)).SequenceEqual(alphaBefore)).IsTrue();
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("unavailablePackages").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task DamagedPackageProduces503AndIsPreservedUntilRepaired()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        await using var server = await StartAsync();
        var version = await SingleVersionAsync(server.Client, "alpha");
        var path = CachedPackagePath(100, 1, "alpha", version);
        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(path, "damaged-package");
        using (var response = await server.Client.GetAsync(PackageUrl("alpha", version)))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var health = await GetJsonAsync(server.Client, "/health"))
        {
            await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
            await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("incomplete or invalid");
        }
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("damaged-package");
        await File.WriteAllBytesAsync(path, original);
        await RetryUntilHealthyAsync(server.Client, validateContent: true);
        await Assert.That((await server.Client.GetByteArrayAsync(PackageUrl("alpha", version))).SequenceEqual(original)).IsTrue();
        using var recovered = await GetJsonAsync(server.Client, "/health");
        await Assert.That(recovered.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
    }

    [Test]
    public async Task PackagesRemainResolvableWhenBuildVersionMetadataIsMissing()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        File.Delete(Path.Combine(CacheDirectory, "catalog.json"));
        File.Delete(Path.Combine(CacheDirectory, "artifacts", _github.VersionArtifact(100).Id + ".zip"));
        var downloads = _github.DownloadCalls;
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        using var package = await offline.Client.GetAsync(PackageUrl("alpha", version));
        await Assert.That(package.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new PackageArchiveReader(new MemoryStream(await package.Content.ReadAsByteArrayAsync()));
        await Assert.That(reader.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);
        using var builds = await offline.Client.GetAsync("/builds");
        await Assert.That(builds.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(offline.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("unavailableBuildMetadata").GetInt32()).IsEqualTo(1);
        await Assert.That(_github.DownloadCalls).IsEqualTo(downloads);
    }

    [Test]
    public async Task ExpiredArtifactsAlreadyOnDiskRemainUsableOffline()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        _github.ExpiredRuns.Add(100);
        var artifacts = Path.Combine(CacheDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        foreach (var artifact in new[] { _github.VersionArtifact(100), _github.PackagesArtifact(100) })
            File.Copy(_github.ArtifactFile(artifact.Id), Path.Combine(artifacts, artifact.Id + ".zip"));
        string version;
        await using (var online = await StartAsync())
            version = await SingleVersionAsync(online.Client, "alpha");
        Directory.Delete(Path.Combine(CacheDirectory, "packages"), recursive: true);
        File.Delete(Path.Combine(CacheDirectory, "catalog.json"));
        _github.DiscoveryError = new IOException("Synthetic offline failure.");
        _github.FailDownloads = true;
        await using var offline = await StartAsync();
        await Assert.That(await SingleVersionAsync(offline.Client, "alpha")).IsEqualTo(version);
        await Assert.That(_github.DownloadCalls).IsEqualTo(0);
    }

    [Test]
    public async Task PinnedExpiredArtifactWithoutCacheProducesExplicit503()
    {
        _github.Runs = [_github.AddBuild(100, 1, "Alpha")];
        _github.OpenPullRequests = [new(1)];
        _github.ExpiredRuns.Add(100);
        await using var server = await StartAsync(runIds: [100]);
        using var response = await server.Client.GetAsync("/v3/index.json");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
        await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("expired artifacts");
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();
        await Assert.That(_github.DownloadCalls).IsEqualTo(0);
    }

    [Test]
    public async Task MissingOrExpiredUnpinnedArtifactsNeverPublishAnEmptySnapshot()
    {
        var old = _github.AddBuild(100, 1, "Alpha");
        var latest = _github.AddBuild(101, 1, "Alpha");
        _github.Runs = [old];
        _github.OpenPullRequests = [new(1)];
        _github.ExpiredRuns.Add(100);
        await using var server = await StartAsync();
        using (var expired = await server.Client.GetAsync("/v3/index.json"))
            await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var health = await GetJsonAsync(server.Client, "/health"))
            await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("expired artifacts");
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();

        _github.ExpiredRuns.Clear();
        _github.MissingVersionArtifactForRun = 100;
        using (var missing = await server.Client.GetAsync("/v3/index.json"))
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using (var health = await GetJsonAsync(server.Client, "/health"))
            await Assert.That(health.RootElement.GetProperty("error").GetString()!).Contains("missing package/version artifacts");
        await Assert.That(File.Exists(SnapshotPath)).IsFalse();

        _github.MissingVersionArtifactForRun = null;
        var oldVersion = await SingleVersionAsync(server.Client, "alpha");
        var snapshot = await File.ReadAllBytesAsync(SnapshotPath);
        var history = await File.ReadAllBytesAsync(RunsPath);
        _github.Runs = [latest, old];
        _github.ExpiredRuns.Add(101);
        ExpireDiscovery(server);
        await Assert.That(await SingleVersionAsync(server.Client, "alpha")).IsEqualTo(oldVersion);
        await WaitForHealthErrorAsync(server.Client, "expired artifacts");
        using var degraded = await GetJsonAsync(server.Client, "/health");
        await Assert.That(degraded.RootElement.GetProperty("error").GetString()!).Contains("expired artifacts");
        await Assert.That((await File.ReadAllBytesAsync(SnapshotPath)).SequenceEqual(snapshot)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(RunsPath)).SequenceEqual(history)).IsTrue();
    }

    [Test]
    public async Task MissingAndUnauthenticatedGhErrorsAreActionableAndRedacted()
    {
        const string marker = "synthetic-sensitive-marker";
        _github.DiscoveryError = new Win32Exception(2, marker);
        await using (var missing = await StartAsync())
        {
            using var response = await missing.Client.GetAsync("/v3/index.json");
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(body).Contains("Install");
            await Assert.That(body).Contains("gh auth login");
            await Assert.That(body.Contains(marker, StringComparison.Ordinal)).IsFalse();
            await Assert.That(missing.Logs.Messages.Any(x => x.Contains(marker, StringComparison.Ordinal))).IsFalse();
        }
        _github.DiscoveryError = new IOException("GitHub CLI failed (4): " + marker);
        await using var unauthenticated = await StartAsync();
        using var denied = await unauthenticated.Client.GetAsync("/v3/index.json");
        var deniedBody = await denied.Content.ReadAsStringAsync();
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(deniedBody).Contains("gh auth login");
        await Assert.That(deniedBody).Contains("gh auth status");
        await Assert.That(deniedBody.Contains(marker, StringComparison.Ordinal)).IsFalse();
        using var health = await GetJsonAsync(unauthenticated.Client, "/health");
        await Assert.That(health.RootElement.ToString().Contains(marker, StringComparison.Ordinal)).IsFalse();
        await Assert.That(unauthenticated.Logs.Messages.Any(x => x.Contains(marker, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task UpstreamDiscoveryExceptionsCannotInjectDiagnosticText()
    {
        const string marker = "synthetic-sensitive-marker";
        _github.DiscoveryError = new DiscoveryUnavailableException(marker);
        await using var server = await StartAsync();
        using var response = await server.Client.GetAsync("/v3/index.json");
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.Contains(marker, StringComparison.Ordinal)).IsFalse();
        using var health = await GetJsonAsync(server.Client, "/health");
        await Assert.That(health.RootElement.ToString().Contains(marker, StringComparison.Ordinal)).IsFalse();
        await Assert.That(server.Logs.Messages.Any(x => x.Contains(marker, StringComparison.Ordinal))).IsFalse();
    }

    private async Task<TestServer> StartAsync(int[]? pullRequests = null, long[]? runIds = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = _directory,
            EnvironmentName = "Production"
        });
        var settings = new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:0",
            ["Proxy:BaseUrl"] = "http://127.0.0.1:1",
            ["Proxy:CacheDirectory"] = CacheDirectory,
            ["Proxy:Repository"] = "fixture/project",
            ["Proxy:Workflow"] = "build.yml"
        };
        if (pullRequests is not null)
            for (var index = 0; index < pullRequests.Length; index++) settings[$"Proxy:PullRequests:{index}"] = pullRequests[index].ToString();
        if (runIds is not null)
            for (var index = 0; index < runIds.Length; index++) settings[$"Proxy:RunIds:{index}"] = runIds[index].ToString();
        builder.Configuration.AddInMemoryCollection(settings);
        var logs = new CaptureLogs();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(_github);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var address = addresses.Single();
            app.Services.GetRequiredService<ProxyOptions>().BaseUrl = address;
            return new TestServer(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) }, logs);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static void ExpireDiscovery(TestServer server) =>
        ((MemoryCache)server.App.Services.GetRequiredService<IMemoryCache>()).Compact(1);

    private async Task WaitForDiscoveryAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_github.DiscoveryCalls < expected && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        if (_github.DiscoveryCalls < expected) throw new TimeoutException("The background discovery did not start.");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path) =>
        JsonDocument.Parse(await client.GetStringAsync(path));

    private static async Task<string> SingleVersionAsync(HttpClient client, string id)
    {
        var versions = await VersionsAsync(client, id);
        await Assert.That(versions.Length).IsEqualTo(1);
        return versions[0];
    }

    private static async Task<string[]> VersionsAsync(HttpClient client, string id)
    {
        using var json = await GetJsonAsync(client, $"/flat/{id}/index.json");
        return json.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()!).ToArray();
    }

    private static async Task WaitForHealthErrorAsync(HttpClient client, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var health = await GetJsonAsync(client, "/health");
            if (health.RootElement.GetProperty("error").ValueKind == JsonValueKind.String
                && health.RootElement.GetProperty("error").GetString()!.Contains(expected, StringComparison.Ordinal))
                return;
            using var response = await client.GetAsync("/v3/index.json");
            await Task.Delay(20);
        }
        throw new TimeoutException("The catalog health did not report the expected discovery error.");
    }

    private static async Task RetryUntilHealthyAsync(HttpClient client, bool validateContent = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/v3/index.json");
            if (validateContent)
            {
                using var probe = await client.GetAsync("/flat/recovery-probe/index.json");
            }
            using var health = await GetJsonAsync(client, "/health");
            if (health.RootElement.GetProperty("status").GetString() == "ok") return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Discovery did not recover.");
    }

    private static async Task RetryUntilMembershipAsync(HttpClient client, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/v3/index.json");
            using var health = await GetJsonAsync(client, "/health");
            if (health.RootElement.GetProperty("membership").GetString() == expected) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Discovery did not establish the expected membership.");
    }

    private static string PackageUrl(string id, string version) => $"/flat/{id}/{version}/{id}.{version}.nupkg";

    private string CachedPackagePath(long runId, int pullRequest, string id, string version) =>
        Path.Combine(CacheDirectory, "packages", "discovered", $"{pullRequest}.{runId}.1.{_github.PackagesArtifact(runId).Id}",
            $"{id}.{version}.nupkg");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed record TestServer(WebApplication App, HttpClient Client, CaptureLogs Logs) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null) messages.Enqueue(exception.ToString());
            }
        }
    }
}