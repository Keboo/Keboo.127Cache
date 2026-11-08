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
