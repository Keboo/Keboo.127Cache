using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Keboo._127Cache.Tests;

public sealed class CliConfigurationTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(
        "artifacts", "cli-configuration-tests", Guid.NewGuid().ToString("N")));

    [Test]
    public async Task ServeLoadsUserAndExplicitFilesBeforeEnvironmentOverrides()
    {
        var profile = Path.Combine(_directory, "profile");
        var configFolder = Path.Combine(profile, OperatingSystem.IsWindows() ? "Keboo.127Cache" : "keboo-127cache");
        var cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(configFolder);
        Directory.CreateDirectory(Path.Combine(_directory, "work"));
        var urls = new HashSet<string>();
        while (urls.Count < 3) urls.Add(FreeUrl());
        var userUrl = urls.ElementAt(0);
        var explicitUrl = urls.ElementAt(1);
        var environmentUrl = urls.ElementAt(2);
        File.WriteAllText(Path.Combine(configFolder, "config.json"), JsonSerializer.Serialize(new
        {
            Server = new { BaseUrl = userUrl, CacheDirectory = cache },
            Sources = Array.Empty<object>()
        }));
        var explicitFile = Path.Combine(_directory, "explicit.json");
        File.WriteAllText(explicitFile, JsonSerializer.Serialize(new { Server = new { BaseUrl = explicitUrl } }));

        await AssertServesAsync(profile, null, null, userUrl);
        await Assert.That(File.Exists(Path.Combine(cache, ".lock"))).IsTrue();
        await AssertServesAsync(profile, explicitFile, null, explicitUrl);
        await AssertServesAsync(profile, explicitFile, environmentUrl, environmentUrl);
    }

    [Test]
    public async Task ServeRejectsMultipleSourcesRatherThanSilentlyServingOnlyOne()
    {
        var profile = Path.Combine(_directory, "profile");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(_directory, "work"));
        var explicitFile = Path.Combine(_directory, "multiple.json");
        File.WriteAllText(explicitFile, JsonSerializer.Serialize(new
        {
            Server = new { CacheDirectory = Path.Combine(_directory, "cache") },
            Sources = new[]
            {
                new { Name = "one", Repository = "org/one", Workflow = "a.yml", Artifact = "a" },
                new { Name = "two", Repository = "org/two", Workflow = "b.yml", Artifact = "b" }
            }
        }));
        using var process = Process.Start(StartInfo(profile, explicitFile, FreeUrl()))
            ?? throw new InvalidOperationException("Could not start the CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var output = await stdout + await stderr;
        await Assert.That(process.ExitCode).IsNotEqualTo(0);
        await Assert.That(output).Contains("Multiple Sources");
        await Assert.That(output).Contains("#9");
    }

    private ProcessStartInfo StartInfo(string profile, string? explicitFile, string? environmentUrl)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("serve");
        if (explicitFile is not null)
        {
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(explicitFile);
        }
        foreach (var name in start.Environment.Keys.Where(name =>
            name.StartsWith("Server__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Sources__", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Proxy__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["APPDATA"] = profile;
        start.Environment["XDG_CONFIG_HOME"] = profile;
        start.Environment["TEMP"] = Path.Combine(_directory, "work");
        start.Environment["TMP"] = Path.Combine(_directory, "work");
        if (environmentUrl is not null) start.Environment["Server__BaseUrl"] = environmentUrl;
        return start;
    }

    private async Task AssertServesAsync(string profile, string? explicitFile, string? environmentUrl, string expectedUrl)
    {
        using var process = Process.Start(StartInfo(profile, explicitFile, environmentUrl))
            ?? throw new InvalidOperationException("Could not start the CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var ready = false;
        string? advertised = null;
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        try
        {
            for (var i = 0; i < 150 && !process.HasExited; i++)
            {
                try
                {
                    using var index = JsonDocument.Parse(await client.GetStringAsync(expectedUrl + "/v3/index.json"));
                    advertised = index.RootElement.GetProperty("resources")[0].GetProperty("@id").GetString();
                    using var builds = JsonDocument.Parse(await client.GetStringAsync(expectedUrl + "/builds"));
                    if (builds.RootElement.GetArrayLength() != 0)
                        throw new InvalidOperationException("The unconfigured CLI feed was not empty.");
                    ready = true;
                    break;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(100);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var output = await stdout;
        var error = await stderr;
        if (!ready) throw new InvalidOperationException($"CLI did not listen at {expectedUrl}.\n{output}\n{error}");
        await Assert.That(advertised).IsEqualTo(expectedUrl + "/flat/");
    }

    private static string FreeUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
