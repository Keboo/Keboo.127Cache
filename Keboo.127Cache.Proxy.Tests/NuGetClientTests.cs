using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NuGet.Packaging;
using NuGet.Versioning;

namespace Keboo._127Cache.Proxy.Tests;

public sealed class NuGetClientTests
{
    private static CancellationToken Cancellation => TestContext.Current!.Execution.CancellationToken;

    [Test]
    [Category("EndToEnd")]
    public async Task StablePackageRestoresWithItsRewrittenInArtifactDependency()
    {
        await using var fixture = new NuGetClientFixture();
        await fixture.StartAsync(includeOlderRun: false, Cancellation);
        const string pluginVersion = "2.0.0-pr.755.run.r0000000000000000100.attempt.1.artifact.2";
        const string libraryVersion = "1.3.67-pr.755.run.r0000000000000000100.attempt.1.artifact.2";

        using var assets = await fixture.RestoreAsync("Example.Plugin", pluginVersion, Cancellation);
        var target = assets.RootElement.GetProperty("targets").GetProperty("net10.0");
        await Assert.That(target.TryGetProperty($"Example.Plugin/{pluginVersion}", out _)).IsTrue();
        await Assert.That(target.TryGetProperty($"Velopack/{libraryVersion}", out _)).IsTrue();
        await Assert.That(target.TryGetProperty("Newtonsoft.Json/13.0.4", out _)).IsTrue();

        using var plugin = new PackageArchiveReader(fixture.InstalledPackage("Example.Plugin", pluginVersion));
        await Assert.That(plugin.GetIdentity().Version.ToNormalizedString()).IsEqualTo(pluginVersion);
        var group = await Assert.That(plugin.NuspecReader.GetDependencyGroups()).HasSingleItem();
        var dependency = await Assert.That(group.Packages).HasSingleItem();
        await Assert.That(dependency.Id).IsEqualTo("Velopack");
        await Assert.That(dependency.VersionRange.ToNormalizedString())
            .IsEqualTo(VersionRange.Parse($"[{libraryVersion}]").ToNormalizedString());

        using var installed = ZipFile.OpenRead(fixture.InstalledPackage("Velopack", libraryVersion));
        using var payload = installed.GetEntry("runtimes/linux-x64/native/test")!.Open();
        using var reader = new StreamReader(payload, Encoding.UTF8);
        await Assert.That(await reader.ReadToEndAsync(Cancellation)).IsEqualTo("unchanged-payload");
    }

    [Test]
    [Category("EndToEnd")]
    public async Task StableFloatingRangeRestoresTheNewestRun()
    {
        await using var fixture = new NuGetClientFixture();
        await fixture.StartAsync(includeOlderRun: true, Cancellation);
        const string newest = "1.3.67-pr.755.run.r0000000000000000100.attempt.1.artifact.2";
        const string older = "1.3.67-pr.755.run.r0000000000000000099.attempt.1.artifact.3";

        var available = await fixture.GetVersionsAsync("Velopack", Cancellation);
        await Assert.That(available).DoesNotContain(older);
        await Assert.That(available).Contains(newest);
        using var assets = await fixture.RestoreAsync("Velopack", "1.3.67-pr.755.*", Cancellation);
        var target = assets.RootElement.GetProperty("targets").GetProperty("net10.0");
        await Assert.That(target.TryGetProperty($"Velopack/{newest}", out _)).IsTrue();
        await Assert.That(target.TryGetProperty($"Velopack/{older}", out _)).IsFalse();
        using var package = new PackageArchiveReader(fixture.InstalledPackage("Velopack", newest));
        await Assert.That(package.GetIdentity().Version.ToNormalizedString()).IsEqualTo(newest);
    }
}

internal sealed class NuGetClientFixture : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "k127-nuget-e2e-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string? _configFile;

    public string PackagesDirectory => Path.Combine(_root, "consumer-packages");

    public async Task StartAsync(bool includeOlderRun, CancellationToken cancellationToken, string? additionalPackagePath = null)
    {
        Directory.CreateDirectory(_root);
        var github = new FakeGitHub(_root, "1.3.67", additionalPackagePath) { OpenPullRequests = [new(755)] };
        var externalSource = Path.Combine(_root, "external-source");
        Directory.CreateDirectory(externalSource);
        FakeGitHub.WritePackage(Path.Combine(externalSource, "Newtonsoft.Json.13.0.4.nupkg"), "Newtonsoft.Json", "13.0.4", null);

        var configuration = new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:0",
            ["Proxy:Repository"] = "fixture/project",
            ["Proxy:CacheDirectory"] = Path.Combine(_root, "server-cache"),
            ["Proxy:PullRequests:0"] = "755"
        };
        if (includeOlderRun) configuration["Proxy:RunIds:0"] = "99";
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddProxy(builder.Configuration);
        builder.Services.AddSingleton<IGitHubClient>(github);
        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.MapProxy();
        await _app.StartAsync(cancellationToken);
        var address = _app.Urls.Single();
        _app.Services.GetRequiredService<ProxyOptions>().BaseUrl = address;

        _configFile = Path.Combine(_root, "NuGet.config");
        new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "proxy"), new XAttribute("value", address + "/v3/index.json"),
                        new XAttribute("allowInsecureConnections", "true")),
                    new XElement("add", new XAttribute("key", "external"), new XAttribute("value", externalSource))),
                new XElement("packageSourceMapping",
                    new XElement("packageSource", new XAttribute("key", "proxy"),
                        new XElement("package", new XAttribute("pattern", "Example.Plugin")),
                        new XElement("package", new XAttribute("pattern", "Velopack"))),
                    new XElement("packageSource", new XAttribute("key", "external"),
                        new XElement("package", new XAttribute("pattern", "Newtonsoft.Json"))))))
            .Save(_configFile);
    }

    public async Task<string[]> GetVersionsAsync(string id, CancellationToken cancellationToken)
    {
        if (_app is null) throw new InvalidOperationException("The feed has not been started.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(_app.Urls.Single()) };
        using var response = JsonDocument.Parse(await client.GetStringAsync($"/flat/{id.ToLowerInvariant()}/index.json", cancellationToken));
        return response.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()!).ToArray();
    }

    public async Task<JsonDocument> RestoreAsync(string id, string version, CancellationToken cancellationToken)
    {
        if (_configFile is null) throw new InvalidOperationException("The feed has not been started.");
        var consumer = Path.Combine(_root, "consumer");
        Directory.CreateDirectory(consumer);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "repo.global.json"), Path.Combine(consumer, "global.json"), overwrite: true);
        var project = Path.Combine(consumer, "Consumer.csproj");
        new XDocument(
            new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("NuGetAudit", "false")),
                new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", version)))))
            .Save(project);

        await RunProcessAsync("dotnet", consumer,
            ["restore", project, "--configfile", _configFile, "--packages", PackagesDirectory, "--no-cache", "--verbosity", "minimal"],
            cancellationToken);
        return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(consumer, "obj", "project.assets.json"), cancellationToken));
    }

    public async Task<string> CreateToolPackageAsync(CancellationToken cancellationToken)
    {
        var producer = Path.Combine(_root, "tool-producer");
        Directory.CreateDirectory(producer);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "repo.global.json"), Path.Combine(producer, "global.json"), overwrite: true);
        var config = Path.Combine(producer, "NuGet.config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear")))).Save(config);
        var project = Path.Combine(producer, "Example.Tool.csproj");
        new XDocument(
            new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("PackageId", "Example.Tool"),
                    new XElement("Version", "3.1.4"),
                    new XElement("Authors", "test"),
                    new XElement("Description", "Test tool package"),
                    new XElement("PackAsTool", "true"),
                    new XElement("ToolCommandName", "example-fixture-tool"),
                    new XElement("NuGetAudit", "false"))))
            .Save(project);
        await File.WriteAllTextAsync(Path.Combine(producer, "Program.cs"),
            "System.Console.WriteLine(\"example-tool-executed\");", cancellationToken);
        await RunProcessAsync("dotnet", producer,
            ["restore", project, "--configfile", config, "--packages", PackagesDirectory, "--no-cache", "--verbosity", "minimal"],
            cancellationToken);
        var output = Path.Combine(producer, "packages");
        await RunProcessAsync("dotnet", producer,
            ["pack", project, "--no-restore", "--configuration", "Release", "--output", output, "--verbosity", "minimal"],
            cancellationToken);
        return Path.Combine(output, "Example.Tool.3.1.4.nupkg");
    }

    public async Task InstallToolAsync(string id, string version, CancellationToken cancellationToken)
    {
        if (_app is null || _configFile is null) throw new InvalidOperationException("The feed has not been started.");
        var source = _app.Urls.Single() + "/v3/index.json";
        var config = Path.Combine(_root, "Tool.NuGet.config");
        // NuGet requires HTTP sources to be allowed in configuration, even with --add-source.
        new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "proxy"), new XAttribute("value", source),
                        new XAttribute("allowInsecureConnections", "true")))))
            .Save(config);
        await RunProcessAsync("dotnet", _root,
            ["tool", "install", id, "--tool-path", Path.Combine(_root, "tools"),
                "--version", version, "--add-source", source,
                "--configfile", config, "--no-http-cache", "--verbosity", "minimal"],
            cancellationToken);
    }

    public Task<string> ListToolsAsync(CancellationToken cancellationToken) =>
        RunProcessAsync("dotnet", _root, ["tool", "list", "--tool-path", Path.Combine(_root, "tools")], cancellationToken);

    public Task<string> RunToolAsync(string command, CancellationToken cancellationToken) =>
        RunProcessAsync(Path.Combine(_root, "tools", command + (OperatingSystem.IsWindows() ? ".exe" : "")),
            _root, [], cancellationToken);

    private async Task<string> RunProcessAsync(string executable, string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(_root, "dotnet-home");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        start.Environment["NUGET_PACKAGES"] = PackagesDirectory;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(_root, "http-cache");
        start.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(_root, "plugins-cache");
        start.Environment.Remove("NUGET_FALLBACK_PACKAGES");
        start.Environment.Remove("NUGET_ADDITIONAL_PACKAGES");
        start.Environment.Remove("RestoreSources");
        foreach (var proxy in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            start.Environment.Remove(proxy);
        start.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        start.Environment["no_proxy"] = "localhost,127.0.0.1,::1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} {string.Join(" ", arguments)} failed ({process.ExitCode}).\n{stdout}\n{stderr}");
        return stdout;
    }

    public string InstalledPackage(string id, string version) =>
        Path.Combine(PackagesDirectory, id.ToLowerInvariant(), version.ToLowerInvariant(), $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}