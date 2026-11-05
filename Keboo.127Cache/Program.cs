using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Keboo._127Cache;

public sealed class Program
{
    public static Task<int> Main(string[] args) => BuildCommandLine().Parse(args).InvokeAsync();

    public static RootCommand BuildCommandLine()
    {
        var configOption = new Option<FileInfo?>("--config")
        {
            Description = "Path to a JSON configuration file."
        };
        var serveCommand = new Command("serve", "Start the local NuGet proxy.")
        {
            configOption
        };
        serveCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunServerAsync(parseResult.GetValue(configOption), cancellationToken);
            return 0;
        });
        return new RootCommand("Serve completed GitHub Actions pull request packages as a local NuGet feed.")
        {
            serveCommand
        };
    }

    private static async Task RunServerAsync(FileInfo? configFile, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        if (configFile is not null)
        {
            builder.Configuration.AddJsonFile(Path.GetFullPath(configFile.FullName), optional: false, reloadOnChange: false)
                .AddEnvironmentVariables();
        }
        builder.Services.AddProxy(builder.Configuration);
        var app = builder.Build();
        var options = app.Services.GetRequiredService<ProxyOptions>();
        Directory.CreateDirectory(options.CacheDirectory);
        using var cacheLock = new FileStream(Path.Combine(options.CacheDirectory, ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        app.UseExceptionHandler();
        app.MapProxy();
        await app.RunAsync(cancellationToken);
    }
}
