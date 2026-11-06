using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;

namespace Keboo._127Cache.Tests;

public sealed class ArtifactIntegrityTests
{
    private static CancellationToken Cancellation => CancellationToken.None;

    [Test]
    public async Task DigestlessArtifactsServeTheFeedAndReuseVerifiedBytes()
    {
        using var fixture = new Fixture();
        await using var app = await StartFeedAsync(fixture);
        using var client = CreateClient(app);

        using var versions = JsonDocument.Parse(await client.GetStringAsync("/flat/example.package/index.json", Cancellation));
        var version = versions.RootElement.GetProperty("versions")[0].GetString()!;
        var bytes = await client.GetByteArrayAsync(
            $"/flat/example.package/{version}/example.package.{version}.nupkg", Cancellation);
        using var package = new PackageArchiveReader(new MemoryStream(bytes));
        await Assert.That(package.GetIdentity().Id).IsEqualTo("Example.Package");
        await Assert.That(package.GetIdentity().Version.ToNormalizedString()).IsEqualTo(version);

        var packagePath = fixture.PathFor(fixture.GitHub.Packages);
        var versionPath = fixture.PathFor(fixture.GitHub.Version);
        await Assert.That(await File.ReadAllTextAsync(packagePath + ".sha256", Cancellation))
            .IsEqualTo(Digest(fixture.GitHub.PackageBytes));
        await Assert.That(await File.ReadAllTextAsync(versionPath + ".sha256", Cancellation))
            .IsEqualTo(Digest(fixture.GitHub.VersionBytes));
        await Assert.That((await File.ReadAllBytesAsync(packagePath, Cancellation))
            .SequenceEqual(fixture.GitHub.PackageBytes)).IsTrue();
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
        await Assert.That(fixture.GitHub.VersionDownloads).IsEqualTo(1);
        await Assert.That(fixture.Log.WarningCount(fixture.GitHub.Packages.Id)).IsEqualTo(1);
        await Assert.That(fixture.Log.WarningCount(fixture.GitHub.Version.Id)).IsEqualTo(1);
        await Assert.That(fixture.Log.HashCount(fixture.GitHub.Packages.Id)).IsEqualTo(1);

        var cache = app.Services.GetRequiredService<ArtifactCache>();
        var paths = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => cache.GetAsync(fixture.GitHub.Packages, Cancellation)));
        await Assert.That(paths.All(path => path == packagePath)).IsTrue();
        await Assert.That(fixture.Log.WarningCount(fixture.GitHub.Packages.Id)).IsEqualTo(1);
        await Assert.That(fixture.Log.HashCount(fixture.GitHub.Packages.Id)).IsEqualTo(1);

        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.None);
            await ExpectFailureAsync<IOException>(() =>
            {
                using var ignored = File.OpenRead(packagePath);
                return Task.CompletedTask;
            });
            await Assert.That(await cache.GetAsync(fixture.GitHub.Packages, Cancellation)).IsEqualTo(packagePath);
        }

        File.SetLastWriteTimeUtc(packagePath, File.GetLastWriteTimeUtc(packagePath).AddMinutes(2));
        await cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        await cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        await Assert.That(fixture.Log.HashCount(fixture.GitHub.Packages.Id)).IsEqualTo(2);

        var restartLog = new RecordingLogger();
        var restarted = new ArtifactCache(fixture.Options, fixture.GitHub, restartLog);
        fixture.GitHub.FailDownloads = true;
        await restarted.GetAsync(fixture.GitHub.Packages, Cancellation);
        await restarted.GetAsync(fixture.GitHub.Packages, Cancellation);
        await Assert.That(restartLog.HashCount(fixture.GitHub.Packages.Id)).IsEqualTo(1);
        await Assert.That(restartLog.WarningCount(fixture.GitHub.Packages.Id)).IsEqualTo(1);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task TamperedCachedZipFailsThroughFeedAfterRestartAndIsNotReplaced()
    {
        using var fixture = new Fixture();
        var path = await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        var original = await File.ReadAllBytesAsync(path, Cancellation);
        var record = await File.ReadAllTextAsync(path + ".sha256", Cancellation);
        var tampered = original.ToArray();
        tampered[tampered.Length / 2] ^= 0x40;
        await ReplaceBytesAndChangeTimestampAsync(path, tampered);

        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
        await using var app = await StartFeedAsync(fixture);
        using var client = CreateClient(app);
        using var rejected = await client.GetAsync("/flat/example.package/index.json", Cancellation);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await File.ReadAllBytesAsync(path, Cancellation)).SequenceEqual(tampered)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation)).IsEqualTo(record);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
        await Assert.That(Directory.Exists(Path.Combine(fixture.Options.CacheDirectory, "packages", "discovered"))).IsFalse();

        await ReplaceBytesAndChangeTimestampAsync(path, original);
        using var recovered = await client.GetAsync("/flat/example.package/index.json", Cancellation);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task MissingOrCorruptRecordsFailClosedWithoutChangingTheCachedZip()
    {
        using var fixture = new Fixture();
        var path = await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        var sidecar = path + ".sha256";
        var digest = await File.ReadAllTextAsync(sidecar, Cancellation);

        File.Delete(sidecar);
        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
        var restarted = new ArtifactCache(fixture.Options, fixture.GitHub);
        await ExpectFailureAsync<InvalidDataException>(() => restarted.GetAsync(fixture.GitHub.Packages, Cancellation));
        foreach (var invalid in new[] { "", digest[..63], new string('G', 64), (digest[0] == 'A' ? "B" : "A") + digest[1..] })
        {
            await File.WriteAllTextAsync(sidecar, invalid, Cancellation);
            await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
            await Assert.That(await File.ReadAllTextAsync(sidecar, Cancellation)).IsEqualTo(invalid);
            await Assert.That((await File.ReadAllBytesAsync(path, Cancellation))
                .SequenceEqual(fixture.GitHub.PackageBytes)).IsTrue();
        }

        await File.WriteAllTextAsync(sidecar, digest, Cancellation);
        await Assert.That(await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation)).IsEqualTo(path);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task InterruptedDownloadsAndOrphanedRecordsRecoverSafely()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor(fixture.GitHub.Packages);
        fixture.GitHub.InterruptNext();
        await ExpectFailureAsync<IOException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".sha256")).IsFalse();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);

        using var cancellation = new CancellationTokenSource();
        fixture.GitHub.InterruptNext(cancellation.Cancel);
        await ExpectCancellationAsync(
            () => fixture.Cache.GetAsync(fixture.GitHub.Packages, cancellation.Token));
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".sha256")).IsFalse();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);

        var orphanZip = path + ".other-writer.tmp";
        var orphanRecord = path + ".sha256.other-writer.tmp";
        await File.WriteAllTextAsync(orphanZip, "partial download", Cancellation);
        await File.WriteAllTextAsync(orphanRecord, "partial record", Cancellation);
        var digest = Digest(fixture.GitHub.PackageBytes);
        await File.WriteAllTextAsync(path + ".sha256", digest, Cancellation);

        var restarted = new ArtifactCache(fixture.Options, fixture.GitHub, fixture.Log);
        await Assert.That(await restarted.GetAsync(fixture.GitHub.Packages, Cancellation)).IsEqualTo(path);
        await Assert.That((await File.ReadAllBytesAsync(path, Cancellation))
            .SequenceEqual(fixture.GitHub.PackageBytes)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation)).IsEqualTo(digest);
        await Assert.That(File.Exists(orphanZip)).IsTrue();
        await Assert.That(File.Exists(orphanRecord)).IsTrue();
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(3);
    }

    [Test]
    public async Task OrphanedBadRecordCannotPoisonAReplacementDownload()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor(fixture.GitHub.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sidecar = path + ".sha256";
        await File.WriteAllTextAsync(sidecar, "partial", Cancellation);
        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(0);

        var wrong = new string('A', 64);
        await File.WriteAllTextAsync(sidecar, wrong, Cancellation);
        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(sidecar, Cancellation)).IsEqualTo(wrong);
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationAfterDownloadDoesNotPublishAPartialRecord()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Log.OnHash = cancellation.Cancel;
        var path = fixture.PathFor(fixture.GitHub.Packages);

        await ExpectCancellationAsync(
            () => fixture.Cache.GetAsync(fixture.GitHub.Packages, cancellation.Token));
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".sha256")).IsFalse();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);

        fixture.Log.OnHash = null;
        await Assert.That(await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation)).IsEqualTo(path);
        await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation))
            .IsEqualTo(Digest(fixture.GitHub.PackageBytes));
    }

    [Test]
    public async Task TemporarilyLockedCacheFilesCanBeRetriedOrCancelled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var path = await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        var record = path + ".sha256";
        var digest = await File.ReadAllTextAsync(record, Cancellation);
        using var locked = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.None);

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await ExpectCancellationAsync(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, cancellation.Token));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = fixture.Cache.GetAsync(fixture.GitHub.Packages, deadline.Token);
        await Task.Delay(50);
        await Assert.That(pending.IsCompleted).IsFalse();
        locked.Dispose();
        await Assert.That(await pending).IsEqualTo(path);
        await Assert.That(await File.ReadAllTextAsync(record, Cancellation)).IsEqualTo(digest);

        using var lockedZip = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var restarted = new ArtifactCache(fixture.Options, fixture.GitHub);
        var pendingZip = restarted.GetAsync(fixture.GitHub.Packages, deadline.Token);
        await Task.Delay(50);
        await Assert.That(pendingZip.IsCompleted).IsFalse();
        lockedZip.Dispose();
        await Assert.That(await pendingZip).IsEqualTo(path);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
    }

    [Test]
    public async Task SuppliedDigestsRemainAuthoritativeAndUnsupportedDigestsNeverFallBack()
    {
        using var fixture = new Fixture();
        var correct = fixture.GitHub.Packages with { Digest = "sha256:" + Digest(fixture.GitHub.PackageBytes).ToLowerInvariant() };
        var incorrect = correct with { Digest = "sha256:" + new string('0', 64) };
        var path = fixture.PathFor(correct);
        var unsupported = new[] { "", "sha512:abcd", "sha256:", "sha256:" + new string('G', 64), "SHA256:" + new string('A', 64) };
        foreach (var digest in unsupported)
            await ExpectFailureAsync<InvalidDataException>(
                () => fixture.Cache.GetAsync(correct with { Digest = digest }, Cancellation));
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(0);
        await Assert.That(File.Exists(path)).IsFalse();
        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(incorrect, Cancellation));
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".sha256")).IsFalse();
        await Assert.That(await fixture.Cache.GetAsync(correct, Cancellation)).IsEqualTo(path);

        foreach (var digest in unsupported)
            await ExpectFailureAsync<InvalidDataException>(
                () => fixture.Cache.GetAsync(correct with { Digest = digest }, Cancellation));
        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(incorrect, Cancellation));
        await Assert.That(await fixture.Cache.GetAsync(correct, Cancellation)).IsEqualTo(path);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(2);
        await Assert.That(fixture.Log.WarningCount(correct.Id)).IsEqualTo(0);

        await File.WriteAllTextAsync(path + ".sha256", "corrupt", Cancellation);
        await Assert.That(await fixture.Cache.GetAsync(correct, Cancellation)).IsEqualTo(path);
        var restarted = new ArtifactCache(fixture.Options, fixture.GitHub);
        fixture.GitHub.FailDownloads = true;
        await Assert.That(await restarted.GetAsync(correct, Cancellation)).IsEqualTo(path);
        await ExpectFailureAsync<InvalidDataException>(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
        await Assert.That((await File.ReadAllBytesAsync(path, Cancellation))
            .SequenceEqual(fixture.GitHub.PackageBytes)).IsTrue();
    }

    [Test]
    public async Task ConcurrentReadersAndWritersNeverPublishMismatchedPairs()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            using var fixture = new Fixture();
            var rendezvous = new DownloadRendezvous();
            var otherGitHub = new FakeGitHub("different bytes");
            fixture.GitHub.Rendezvous = rendezvous;
            otherGitHub.Rendezvous = rendezvous;
            var otherCache = new ArtifactCache(fixture.Options, otherGitHub, new RecordingLogger());

            var first = CaptureAsync(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation));
            var second = CaptureAsync(() => otherCache.GetAsync(otherGitHub.Packages, Cancellation));
            await rendezvous.Ready.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
            rendezvous.Release();
            var results = await Task.WhenAll(first, second);
            await Assert.That(results.Count(result => result.Path is not null)).IsEqualTo(1);
            var failure = results.Single(result => result.Error is not null).Error!;
            if (failure is not InvalidDataException)
                throw new InvalidOperationException($"Unexpected concurrent failure in iteration {iteration}: {failure}", failure);

            var path = fixture.PathFor(fixture.GitHub.Packages);
            var actual = await File.ReadAllBytesAsync(path, Cancellation);
            await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation)).IsEqualTo(Digest(actual));
            await Assert.That(actual.SequenceEqual(fixture.GitHub.PackageBytes)
                || actual.SequenceEqual(otherGitHub.PackageBytes)).IsTrue();
            await Assert.That(await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation)).IsEqualTo(path);
        }
    }

    [Test]
    public async Task PublicationWaitsForAnExclusiveLockAndCanBeCancelled()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor(fixture.GitHub.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var hashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Log.OnHash = () => hashed.TrySetResult();

        var pending = fixture.Cache.GetAsync(fixture.GitHub.Packages, cancellation.Token);
        await hashed.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        await Task.Delay(150, Cancellation);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".sha256")).IsFalse();

        var otherGitHub = new FakeGitHub("same", fixture.GitHub.PackageBytes);
        var otherHash = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherLog = new RecordingLogger { OnHash = () => otherHash.TrySetResult() };
        var otherCache = new ArtifactCache(fixture.Options, otherGitHub, otherLog);
        using var otherCancellation = new CancellationTokenSource();
        var queued = otherCache.GetAsync(otherGitHub.Packages, otherCancellation.Token);
        await otherHash.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        await Assert.That(queued.IsCompleted).IsFalse();
        otherCancellation.Cancel();
        await ExpectCancellationAsync(() => queued);
        await Assert.That(pending.IsCompleted).IsFalse();

        cancellation.Cancel();
        await ExpectCancellationAsync(() => pending);
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);

        held.Dispose();
        var published = await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        await Assert.That(published).IsEqualTo(path);
        await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation))
            .IsEqualTo(Digest(await File.ReadAllBytesAsync(path, Cancellation)));
    }

    [Test]
    public async Task ABlockedArtifactDoesNotPreventAnotherArtifactFromPublishing()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var blockedPath = fixture.PathFor(fixture.GitHub.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        using var held = new FileStream(blockedPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Log.OnHash = () => hashed.TrySetResult();
        var blocked = fixture.Cache.GetAsync(fixture.GitHub.Packages, deadline.Token);
        await hashed.Task.WaitAsync(deadline.Token);

        var otherArtifact = new Artifact(3, "other-packages", false, null);
        var otherCache = new ArtifactCache(fixture.Options, fixture.GitHub);
        var otherPath = await otherCache.GetAsync(otherArtifact, deadline.Token);
        await Assert.That(otherPath).IsEqualTo(fixture.PathFor(otherArtifact));
        await Assert.That(await File.ReadAllTextAsync(otherPath + ".sha256", deadline.Token))
            .IsEqualTo(Digest(await File.ReadAllBytesAsync(otherPath, deadline.Token)));
        await Assert.That(blocked.IsCompleted).IsFalse();
        held.Dispose();
        await Assert.That(await blocked).IsEqualTo(blockedPath);
    }

    [Test]
    public async Task AZipPublishedWhileWaitingIsVerifiedBeforeCreatingARecord()
    {
        foreach (var matches in new[] { true, false })
        {
            using var fixture = new Fixture();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var path = fixture.PathFor(fixture.GitHub.Packages);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var hashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Log.OnHash = () => hashed.TrySetResult();
            var supplied = fixture.GitHub.Packages with { Digest = "sha256:" + Digest(fixture.GitHub.PackageBytes) };
            var pending = fixture.Cache.GetAsync(supplied, deadline.Token);
            await hashed.Task.WaitAsync(deadline.Token);
            var publishedBytes = matches ? fixture.GitHub.PackageBytes : new FakeGitHub("different").PackageBytes;
            await File.WriteAllBytesAsync(path, publishedBytes, deadline.Token);
            held.Dispose();

            if (matches)
                await Assert.That(await pending).IsEqualTo(path);
            else
                await ExpectFailureAsync<InvalidDataException>(() => pending);
            await Assert.That((await File.ReadAllBytesAsync(path, deadline.Token)).SequenceEqual(publishedBytes)).IsTrue();
            await Assert.That(File.Exists(path + ".sha256")).IsFalse();
        }
    }

    [Test]
    public async Task ConflictingWritersInIndependentProcessesPublishOneMatchingPair()
    {
        var childDirectory = Environment.GetEnvironmentVariable("K127_INTEGRITY_CHILD_DIRECTORY");
        if (childDirectory is not null)
        {
            await RunWriterChildAsync(childDirectory);
            return;
        }

        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var path = fixture.PathFor(fixture.GitHub.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var release = Path.Combine(fixture.Directory, "release-writers");
        var payloads = new[]
        {
            fixture.GitHub.PackageBytes,
            new FakeGitHub("another download").PackageBytes,
            new FakeGitHub("a third download").PackageBytes
        };
        var children = new List<TestChild>();
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task? observer = null;
        try
        {
            for (var i = 0; i < payloads.Length; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, $"payload-{i}"), payloads[i], deadline.Token);
                children.Add(StartTestChild(nameof(ConflictingWritersInIndependentProcessesPublishOneMatchingPair),
                    fixture.Directory, i));
            }

            await WaitForChildSignalsAsync(fixture.Directory, "ready", children, deadline.Token);
            await File.WriteAllTextAsync(release, "go", deadline.Token);
            await WaitForChildSignalsAsync(fixture.Directory, "hashed", children, deadline.Token);
            await Task.Delay(200, deadline.Token);
            foreach (var child in children) await child.EnsureRunningAsync();
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(File.Exists(path + ".sha256")).IsFalse();

            held.Dispose();
            observer = ObservePublishedPairAsync(fixture, path, deadline.Token);
            await Task.WhenAll(children.Select(child => child.RequireExitAsync(0, deadline.Token)));
            await observer;

            var results = await Task.WhenAll(Enumerable.Range(0, payloads.Length)
                .Select(i => File.ReadAllTextAsync(Path.Combine(fixture.Directory, $"result-{i}"), deadline.Token)));
            await Assert.That(results.Count(result => result == "success")).IsEqualTo(1);
            await Assert.That(results.Count(result => result == typeof(InvalidDataException).FullName)).IsEqualTo(2);
            var actual = await File.ReadAllBytesAsync(path, deadline.Token);
            await Assert.That(actual.SequenceEqual(payloads[Array.IndexOf(results, "success")])).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(path + ".sha256", deadline.Token)).IsEqualTo(Digest(actual));
            await Assert.That(File.Exists(path + ".lock")).IsTrue();
            await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);
        }
        finally
        {
            held.Dispose();
            await File.WriteAllTextAsync(release, "go", Cancellation);
            deadline.Cancel();
            try
            {
                if (observer is not null)
                {
                    try { await observer; }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                await FinishChildrenAsync(children);
            }
        }
    }

    [Test]
    public async Task AbruptProcessExitReleasesLockAndRecoversMatchingOrphanRecord()
    {
        var childDirectory = Environment.GetEnvironmentVariable("K127_INTEGRITY_CHILD_DIRECTORY");
        if (childDirectory is not null)
        {
            await RunLockOwnerChildAsync(childDirectory);
            return;
        }

        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var path = fixture.PathFor(fixture.GitHub.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "owner-digest"),
            Digest(fixture.GitHub.PackageBytes), deadline.Token);
        var child = StartTestChild(nameof(AbruptProcessExitReleasesLockAndRecoversMatchingOrphanRecord),
            fixture.Directory, 0);
        try
        {
            await WaitForChildSignalsAsync(fixture.Directory, "ready", [child], deadline.Token);
            var orphan = path + ".crashed-writer.tmp";
            await Assert.That(File.Exists(path + ".lock")).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(path + ".sha256", deadline.Token))
                .IsEqualTo(Digest(fixture.GitHub.PackageBytes));
            await Assert.That(File.Exists(orphan)).IsTrue();

            using var cancellation = new CancellationTokenSource();
            var firstHash = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Log.OnHash = () => firstHash.TrySetResult();
            var cancelled = fixture.Cache.GetAsync(fixture.GitHub.Packages, cancellation.Token);
            await firstHash.Task.WaitAsync(deadline.Token);
            await Task.Delay(200, deadline.Token);
            await Assert.That(cancelled.IsCompleted).IsFalse();
            cancellation.Cancel();
            await ExpectCancellationAsync(() => cancelled);
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp")
                .SequenceEqual([orphan])).IsTrue();

            var secondHash = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Log.OnHash = () => secondHash.TrySetResult();
            var resumed = fixture.Cache.GetAsync(fixture.GitHub.Packages, deadline.Token);
            await secondHash.Task.WaitAsync(deadline.Token);
            await Task.Delay(200, deadline.Token);
            await Assert.That(resumed.IsCompleted).IsFalse();

            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "exit-owner"), "exit", deadline.Token);
            await child.RequireExitAsync(73, deadline.Token);
            await Assert.That(await resumed).IsEqualTo(path);
            await Assert.That(File.Exists(path + ".lock")).IsTrue();
            await Assert.That(File.Exists(orphan)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(path + ".sha256", deadline.Token))
                .IsEqualTo(Digest(await File.ReadAllBytesAsync(path, deadline.Token)));
            await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(2);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "exit-owner"), "exit", Cancellation);
            deadline.Cancel();
            await FinishChildrenAsync([child]);
        }
    }

    [Test]
    public async Task DisabledUnixFileLockingFailsClosed()
    {
        if (OperatingSystem.IsWindows()) return;
        var childDirectory = Environment.GetEnvironmentVariable("K127_INTEGRITY_CHILD_DIRECTORY");
        if (childDirectory is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var github = new FakeGitHub("unlocked");
            var cache = new ArtifactCache(new ProxyOptions { CacheDirectory = Path.Combine(childDirectory, "cache") }, github);
            var result = await CaptureAsync(() => cache.GetAsync(github.Packages, timeout.Token));
            await File.WriteAllTextAsync(Path.Combine(childDirectory, "result-0"),
                result.Error is IOException error && error.Message.Contains("Exclusive file locking is unavailable",
                    StringComparison.Ordinal) ? "locking unavailable" : result.Error?.ToString() ?? "success",
                timeout.Token);
            return;
        }

        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var path = fixture.PathFor(fixture.GitHub.Packages);
        var child = StartTestChild(nameof(DisabledUnixFileLockingFailsClosed), fixture.Directory, 0,
            disableFileLocking: true);
        try
        {
            await child.RequireExitAsync(0, deadline.Token);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "result-0"), deadline.Token))
                .IsEqualTo("locking unavailable");
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(File.Exists(path + ".sha256")).IsFalse();
            await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);

            await Assert.That(await fixture.Cache.GetAsync(fixture.GitHub.Packages, deadline.Token)).IsEqualTo(path);
            await Assert.That(await File.ReadAllTextAsync(path + ".sha256", deadline.Token))
                .IsEqualTo(Digest(await File.ReadAllBytesAsync(path, deadline.Token)));
        }
        finally
        {
            await FinishChildrenAsync([child]);
        }
    }

    [Test]
    public async Task ConcurrentInstancesConvergeOnOneVerifiedPair()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            using var fixture = new Fixture();
            var rendezvous = new DownloadRendezvous();
            var otherGitHub = new FakeGitHub("unused", fixture.GitHub.PackageBytes);
            fixture.GitHub.Rendezvous = rendezvous;
            otherGitHub.Rendezvous = rendezvous;
            var otherCache = new ArtifactCache(fixture.Options, otherGitHub, new RecordingLogger());
            var supplied = otherGitHub.Packages with { Digest = "sha256:" + Digest(otherGitHub.PackageBytes) };

            var first = Enumerable.Range(0, 8)
                .Select(_ => fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation)).ToArray();
            var second = Enumerable.Range(0, 8)
                .Select(_ => otherCache.GetAsync(supplied, Cancellation)).ToArray();
            await rendezvous.Ready.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
            rendezvous.Release();
            var paths = await Task.WhenAll(first.Concat(second));
            var path = fixture.PathFor(fixture.GitHub.Packages);
            await Assert.That(paths.All(result => result == path)).IsTrue();
            await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
            await Assert.That(otherGitHub.PackageDownloads).IsEqualTo(1);
            await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation))
                .IsEqualTo(Digest(await File.ReadAllBytesAsync(path, Cancellation)));
            await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ExpiredArtifactsAreUsedOnlyWhenAlreadyCachedAndVerified()
    {
        using var fixture = new Fixture();
        var expired = fixture.GitHub.Packages with { Expired = true };
        await ExpectFailureAsync<IOException>(() => fixture.Cache.GetAsync(expired, Cancellation));
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(0);
        var path = await fixture.Cache.GetAsync(fixture.GitHub.Packages, Cancellation);
        await Assert.That(await fixture.Cache.GetAsync(expired, Cancellation)).IsEqualTo(path);
        await Assert.That(fixture.GitHub.PackageDownloads).IsEqualTo(1);
    }

    private static async Task<WebApplication> StartFeedAsync(Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = fixture.Directory,
            EnvironmentName = "Production"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:CacheDirectory"] = fixture.Options.CacheDirectory,
            ["Proxy:PullRequests:0"] = "755"
        });
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(fixture.GitHub);
        builder.Services.AddSingleton<ILogger<ArtifactCache>>(fixture.Log);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapProxy();
        await app.StartAsync(Cancellation);
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new HttpClient { BaseAddress = new Uri(addresses.Single()) };
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task ReplaceBytesAndChangeTimestampAsync(string path, byte[] bytes)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, bytes, Cancellation);
        File.SetLastWriteTimeUtc(path, lastWrite.AddMinutes(2));
    }

    private static async Task ExpectFailureAsync<TException>(Func<Task> operation) where TException : Exception
    {
        Exception? error = null;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            error = exception;
        }
        await Assert.That(error?.GetType()).IsEqualTo(typeof(TException));
    }

    private static async Task ExpectCancellationAsync(Func<Task> operation)
    {
        var cancelled = false;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        await Assert.That(cancelled).IsTrue();
    }

    private static async Task<(string? Path, Exception? Error)> CaptureAsync(Func<Task<string>> operation)
    {
        try
        {
            return (await operation(), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static async Task RunWriterChildAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var index = int.Parse(Environment.GetEnvironmentVariable("K127_INTEGRITY_CHILD_INDEX")!);
        var payload = await File.ReadAllBytesAsync(Path.Combine(directory, $"payload-{index}"), deadline.Token);
        var github = new FakeGitHub("child", payload)
        {
            AfterDownload = async token =>
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"ready-{index}"), "ready", token);
                await WaitForFileAsync(Path.Combine(directory, "release-writers"), token);
            }
        };
        var log = new RecordingLogger
        {
            OnHash = () => File.WriteAllText(Path.Combine(directory, $"hashed-{index}"), "hashed")
        };
        var cache = new ArtifactCache(new ProxyOptions { CacheDirectory = Path.Combine(directory, "cache") }, github, log);
        var artifact = index == 2
            ? github.Packages with { Digest = "sha256:" + Digest(payload).ToLowerInvariant() }
            : github.Packages;
        var result = await CaptureAsync(() => cache.GetAsync(artifact, deadline.Token));
        await File.WriteAllTextAsync(Path.Combine(directory, $"result-{index}"),
            result.Error?.GetType().FullName ?? "success", deadline.Token);
    }

    private static async Task RunLockOwnerChildAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var path = Path.Combine(directory, "cache", "artifacts", "2.zip");
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var digest = await File.ReadAllTextAsync(Path.Combine(directory, "owner-digest"), deadline.Token);
        await File.WriteAllTextAsync(path + ".sha256", digest, deadline.Token);
        await File.WriteAllTextAsync(path + ".crashed-writer.tmp", "partial download", deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "ready-0"), "ready", deadline.Token);
        await WaitForFileAsync(Path.Combine(directory, "exit-owner"), deadline.Token);
        Environment.Exit(73);
    }

    private static async Task ObservePublishedPairAsync(Fixture fixture, string path, CancellationToken cancellationToken)
    {
        await WaitForFileAsync(path, cancellationToken);
        var github = new FakeGitHub("reader") { FailDownloads = true };
        var reader = new ArtifactCache(fixture.Options, github);
        for (var i = 0; i < 32; i++)
        {
            await reader.GetAsync(github.Packages, cancellationToken);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var record = await File.ReadAllTextAsync(path + ".sha256", cancellationToken);
            if (record != Digest(bytes))
                throw new InvalidDataException($"Published artifact {path} does not match its SHA256 record.");
            await Task.Yield();
        }
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path)) await Task.Delay(20, cancellationToken);
    }

    private static async Task WaitForChildSignalsAsync(
        string directory, string signal, IReadOnlyList<TestChild> children, CancellationToken cancellationToken)
    {
        while (Enumerable.Range(0, children.Count)
            .Any(index => !File.Exists(Path.Combine(directory, $"{signal}-{index}"))))
        {
            foreach (var child in children) await child.EnsureRunningAsync();
            await Task.Delay(20, cancellationToken);
        }
    }

    private static TestChild StartTestChild(string method, string directory, int index, bool disableFileLocking = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(ArtifactIntegrityTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add($"/*/*/ArtifactIntegrityTests/{method}");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(Path.Combine(directory, $"child-results-{method}-{index}"));
        start.ArgumentList.Add("--exit-on-process-exit");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--ansi");
        start.ArgumentList.Add("off");
        start.ArgumentList.Add("--progress");
        start.ArgumentList.Add("off");
        start.Environment["K127_INTEGRITY_CHILD_DIRECTORY"] = directory;
        start.Environment["K127_INTEGRITY_CHILD_INDEX"] = index.ToString();
        if (disableFileLocking) start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start integrity test child.");
        return new TestChild(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }

    private static async Task FinishChildrenAsync(IEnumerable<TestChild> children)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await Task.WhenAll(children.Select(child => child.Process.WaitForExitAsync(deadline.Token)));
        }
        finally
        {
            foreach (var child in children) child.Process.Dispose();
        }
    }

    private sealed record TestChild(Process Process, Task<string> Output, Task<string> Error)
    {
        public async Task EnsureRunningAsync()
        {
            if (Process.HasExited)
                throw new InvalidOperationException(
                    $"Integrity test child exited early ({Process.ExitCode}).\n{await Output}\n{await Error}");
        }

        public async Task RequireExitAsync(int expected, CancellationToken cancellationToken)
        {
            await Process.WaitForExitAsync(cancellationToken);
            if (Process.ExitCode != expected)
                throw new InvalidOperationException(
                    $"Integrity test child exited with {Process.ExitCode}, expected {expected}.\n{await Output}\n{await Error}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(
            System.IO.Directory.GetCurrentDirectory(), "artifacts", "artifact-integrity-tests", Guid.NewGuid().ToString("N"));
        public ProxyOptions Options { get; }
        public FakeGitHub GitHub { get; } = new("original bytes");
        public RecordingLogger Log { get; } = new();
        public ArtifactCache Cache { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Options = new ProxyOptions { CacheDirectory = Path.Combine(Directory, "cache"), PullRequests = [755] };
            Cache = new ArtifactCache(Options, GitHub, Log);
        }

        public string PathFor(Artifact artifact) => Path.Combine(Options.CacheDirectory, "artifacts", $"{artifact.Id}.zip");

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class RecordingLogger : ILogger<ArtifactCache>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string? Event, string Message)> _entries = new();
        public Action? OnHash { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue((logLevel, eventId.Name, formatter(state, exception)));
            if (eventId.Name == "ArtifactSha256Computed") OnHash?.Invoke();
        }

        public int WarningCount(long artifactId) => _entries.Count(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains($"Artifact {artifactId} ", StringComparison.Ordinal));
        public int HashCount(long artifactId) => _entries.Count(entry => entry.Event == "ArtifactSha256Computed"
            && entry.Message.Contains($"artifact {artifactId}.", StringComparison.Ordinal));
    }

    private sealed class DownloadRendezvous
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public Task Ready => _ready.Task;
        public void Release() => _release.TrySetResult();
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeGitHub
        : IGitHubClient
    {
        private readonly ConcurrentDictionary<long, int> _downloads = new();
        private int _interrupt;
        private Action? _onInterrupt;
        public Artifact Version { get; } = new(1, "build-version", false, null);
        public Artifact Packages { get; } = new(2, "packages", false, null);
        public byte[] VersionBytes { get; } = CreateVersionZip();
        public byte[] PackageBytes { get; }
        public bool FailDownloads { get; set; }
        public DownloadRendezvous? Rendezvous { get; set; }
        public Func<CancellationToken, Task>? AfterDownload { get; set; }
        public int VersionDownloads => _downloads.GetValueOrDefault(Version.Id);
        public int PackageDownloads => _downloads.GetValueOrDefault(Packages.Id);

        public FakeGitHub(string marker, byte[]? packageBytes = null) =>
            PackageBytes = packageBytes ?? CreatePackagesZip(marker);

        public void InterruptNext(Action? onInterrupt = null)
        {
            _onInterrupt = onInterrupt;
            Interlocked.Exchange(ref _interrupt, 1);
        }

        public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PullRequestInfo>>([new(755)]);
        public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>([new(100, 1, "head", "completed", "success", "pull_request", [new(755)])]);
        public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Artifact>>([Version, Packages]);

        public async Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
        {
            if (FailDownloads) throw new IOException("Downloads unavailable.");
            _downloads.AddOrUpdate(artifact.Id, 1, (_, value) => value + 1);
            var bytes = artifact.Id == Version.Id ? VersionBytes : PackageBytes;
            if (Interlocked.Exchange(ref _interrupt, 0) == 1)
            {
                await File.WriteAllBytesAsync(destination, bytes[..20], cancellationToken);
                _onInterrupt?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("Interrupted download.");
            }
            await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
            if (artifact.Id == Packages.Id && Rendezvous is not null)
                await Rendezvous.WaitAsync(cancellationToken);
            if (AfterDownload is not null)
                await AfterDownload(cancellationToken);
        }

        private static byte[] CreateVersionZip()
        {
            using var result = new MemoryStream();
            using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("version.txt").Open()))
                writer.Write("1.2.3");
            return result.ToArray();
        }

        private static byte[] CreatePackagesZip(string marker)
        {
            using var package = new MemoryStream();
            using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Example.Package.nuspec").Open()))
                    writer.Write("""
                        <?xml version="1.0" encoding="utf-8"?>
                        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                          <metadata><id>Example.Package</id><version>1.2.3</version><authors>test</authors>
                            <description>Integrity test package</description></metadata>
                        </package>
                        """);
                using var payload = zip.CreateEntry("content/data.txt").Open();
                payload.Write(Encoding.UTF8.GetBytes(marker));
            }
            using var result = new MemoryStream();
            using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = zip.CreateEntry("Example.Package.nupkg", CompressionLevel.NoCompression).Open())
                entry.Write(package.ToArray());
            return result.ToArray();
        }
    }
}
