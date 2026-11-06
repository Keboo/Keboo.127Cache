using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

using Keboo._127Cache;

namespace Keboo._127Cache.Tests;

public sealed class GhCliIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Directory.GetCurrentDirectory(), "TestResults", "gh-integration-" + Guid.NewGuid().ToString("N"));

    [Test]
    public async Task RealGhProcessFailuresKeepServerRunningAndDoNotExposeStderr()
    {
        Directory.CreateDirectory(_directory);
        await CheckGhFailureAsync(authFailure: false);
        await CheckGhFailureAsync(authFailure: true);
    }

    [Test]
    [Arguments("match")]
    [Arguments("mismatch")]
    [Arguments("wrong-id")]
    [Arguments("missing-workflow")]
    public async Task RepositoryWidePinnedRunsMustBelongToConfiguredWorkflow(string scenario)
    {
        var matches = scenario == "match";
        var fixture = Path.Combine(_directory, scenario);
        Directory.CreateDirectory(fixture);
        var fakeGh = Path.Combine(AppContext.BaseDirectory, "fake-gh");
        var executable = Path.Combine(fakeGh, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        await Assert.That(File.Exists(executable)).IsTrue();
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = $"http://127.0.0.1:{port}";
        var cache = Path.Combine(fixture, "cache");
        var config = Path.Combine(fixture, "proxy.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            Urls = address,
            Proxy = new
            {
                Repository = "fixture/project",
                Workflow = "build.yml",
                RunIds = new[] { 100 },
                CacheDirectory = cache,
                BaseUrl = address
            }
        }));
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = Directory.GetParent(runtime)!.Parent!.Parent!.FullName;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            WorkingDirectory = fixture,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "serve", "--config", config })
            start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("Proxy__", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Urls", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("DOTNET_URLS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GH_ENTERPRISE_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GITHUB_ENTERPRISE_TOKEN", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["PATH"] = fakeGh;
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["K127_FAKE_GH_PIN"] = scenario;
        if (!matches) start.Environment["K127_FAKE_GH_RETRY_GATE"] = fixture;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The server process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(4) };
            await WaitForServerAsync(process, client);
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
                var status = health.RootElement.GetProperty("status").GetString();
                var diagnostic = health.RootElement.GetProperty("error").GetString();
                if (status == "ok" || diagnostic?.Contains("workflow", StringComparison.OrdinalIgnoreCase) == true
                    || diagnostic?.Contains("authentication", StringComparison.OrdinalIgnoreCase) == true)
                    break;
                await Task.Delay(50);
            }
            using var response = await client.GetAsync("/v3/index.json");
            if (matches)
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var runs = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cache, "runs.json")));
                await Assert.That(runs.RootElement.GetProperty("runs")[0].GetProperty("workflowVerified").GetBoolean()).IsTrue();
            }
            else
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                await Assert.That(problem.RootElement.GetProperty("detail").GetString()!).Contains("workflow");
                await Assert.That(File.Exists(Path.Combine(cache, "visible-runs.json"))).IsFalse();
                var retryStarted = Path.Combine(fixture, "retry-started");
                var retryDeadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(retryStarted) && DateTime.UtcNow < retryDeadline)
                {
                    using var retry = await client.GetAsync("/v3/index.json");
                    await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                    using var retryProblem = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
                    await Assert.That(retryProblem.RootElement.GetProperty("detail").GetString()!).Contains("workflow");
                    await Task.Delay(20);
                }
                await Assert.That(File.Exists(retryStarted)).IsTrue();
                await File.WriteAllTextAsync(Path.Combine(fixture, "fixed-workflow"), "");
                await File.WriteAllTextAsync(Path.Combine(fixture, "release-retry"), "");
                var recovered = false;
                var recoveryDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < recoveryDeadline)
                {
                    using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
                    if (health.RootElement.GetProperty("status").GetString() == "ok")
                    {
                        await Assert.That(health.RootElement.GetProperty("provenance").GetString()).IsEqualTo("verified");
                        recovered = true;
                        break;
                    }
                    await Task.Delay(50);
                }
                await Assert.That(recovered).IsTrue();
                using var restored = await client.GetAsync("/v3/index.json");
                await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }
        finally
        {
            if (!matches) await File.WriteAllTextAsync(Path.Combine(fixture, "release-retry"), "");
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output;
            await error;
        }
    }

    private async Task CheckGhFailureAsync(bool authFailure)
    {
        const string marker = "synthetic-sensitive-marker";
        var fixture = Path.Combine(_directory, authFailure ? "auth" : "missing");
        Directory.CreateDirectory(fixture);
        var path = authFailure ? Path.Combine(AppContext.BaseDirectory, "fake-gh") : fixture;
        if (authFailure)
        {
            var executable = Path.Combine(path, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
            await Assert.That(File.Exists(executable)).IsTrue();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = $"http://127.0.0.1:{port}";
        var config = Path.Combine(fixture, "proxy.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            Urls = address,
            Proxy = new
            {
                Repository = "fixture/project",
                Workflow = "build.yml",
                CacheDirectory = Path.Combine(fixture, "cache"),
                BaseUrl = address
            }
        }));

        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = Directory.GetParent(runtime)!.Parent!.Parent!.FullName;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            WorkingDirectory = fixture,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("Proxy__", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Urls", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("DOTNET_URLS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GH_ENTERPRISE_TOKEN", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GITHUB_ENTERPRISE_TOKEN", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["PATH"] = path;
        start.Environment["DOTNET_ROOT"] = dotnetRoot;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The server process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(2) };
            await WaitForServerAsync(process, client);
            using var response = await client.GetAsync("/v3/index.json");
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(body).Contains("gh auth login");
            await Assert.That(body.Contains(marker, StringComparison.Ordinal)).IsFalse();
            using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
            await Assert.That(health.RootElement.GetProperty("status").GetString()).IsEqualTo("degraded");
            var error = health.RootElement.GetProperty("error").GetString()!;
            await Assert.That(error).Contains(authFailure ? "authentication failed" : "could not be started");
            await Assert.That(error.Contains(marker, StringComparison.Ordinal)).IsFalse();
            await Assert.That(process.HasExited).IsFalse();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var logs = await standardOutput + await standardError;
        await Assert.That(logs.Contains(marker, StringComparison.Ordinal)).IsFalse();
    }

    private static async Task WaitForServerAsync(Process process, HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Server exited with code {process.ExitCode}.");
            try
            {
                using var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The server did not start while gh was unavailable.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}