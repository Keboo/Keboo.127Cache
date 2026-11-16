using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Keboo.LocalCache.Ui;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache;

public sealed class Program
{
    public static Task<int> Main(string[] args) => BuildCommandLine().Parse(args).InvokeAsync();

    public static RootCommand BuildCommandLine()
    {
        var configOption = new Option<FileInfo?>("--config")
        {
            Description = "Path to a JSON configuration file."
        };
        var openOption = new Option<bool>("--open")
        {
            Description = "Open the web UI in the default browser once the server has started."
        };
        var serveCommand = new Command("serve", "Start the local NuGet proxy and web UI.")
        {
            configOption,
            openOption
        };
        serveCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunServerAsync(parseResult.GetValue(configOption), parseResult.GetValue(openOption), cancellationToken);
            return 0;
        });
        return new RootCommand("Serve completed GitHub Actions pull request packages as a local NuGet feed.")
        {
            serveCommand
        };
    }

    private static async Task RunServerAsync(FileInfo? configFile, bool openBrowser, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        ConfigurationFiles.AddUserAndExplicitConfiguration(builder.Configuration, configFile);
        builder.Services.AddProxy(builder.Configuration,
            new SourceConfigurationLocations(ConfigurationFiles.UserConfigPath(), configFile?.FullName));
        builder.Services.AddWebUiStartup(openBrowser);
        var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
        app.Urls.Clear();
        app.Urls.Add(options.BaseUrl);
        Directory.CreateDirectory(options.CacheDirectory);
        using var cacheLock = new FileStream(Path.Combine(options.CacheDirectory, ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        app.UseExceptionHandler();
        app.MapProxy();
        await app.RunAsync(cancellationToken);
    }
}
