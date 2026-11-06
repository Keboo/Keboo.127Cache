using System.Collections.Concurrent;
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
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(rejected.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
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
        for (var iteration = 0; iteration < 96; iteration++)
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
    public async Task ArtifactPublicationWaitsForAnExclusiveWriterAndCanBeCancelled()
    {
        using var fixture = new Fixture();
        var path = fixture.PathFor(fixture.GitHub.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await ExpectCancellationAsync(() => fixture.Cache.GetAsync(fixture.GitHub.Packages, cancellation.Token));
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(File.Exists(path + ".sha256")).IsFalse();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length).IsEqualTo(0);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = fixture.Cache.GetAsync(fixture.GitHub.Packages, deadline.Token);
        await Task.Delay(50);
        await Assert.That(waiting.IsCompleted).IsFalse();
        held.Dispose();
        await Assert.That(await waiting).IsEqualTo(path);
        var actual = await File.ReadAllBytesAsync(path, Cancellation);
        await Assert.That(await File.ReadAllTextAsync(path + ".sha256", Cancellation)).IsEqualTo(Digest(actual));
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
