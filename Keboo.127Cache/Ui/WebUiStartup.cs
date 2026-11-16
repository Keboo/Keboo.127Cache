using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache.Ui;

public interface IBrowserLauncher
{
    void Open(string url);
}

internal sealed class ProcessBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        // The shell resolves the user's default browser; the URL is always our own loopback address.
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

// Logs where the UI lives once the server is listening and optionally opens it in the default browser.
public sealed class WebUiStartup(
    IOptions<ProxyOptions> options, IHostApplicationLifetime lifetime, IBrowserLauncher launcher, ILogger<WebUiStartup> logger,
    WebUiStartupOptions startup) : IHostedService
{
    public static string UiUrl(ProxyOptions options) => options.BaseUrl.TrimEnd('/') + "/";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(OnStarted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal void OnStarted()
    {
        var url = UiUrl(options.Value);
        logger.LogInformation("Web UI available at {Url}", url);
        if (!startup.OpenBrowser) return;
        try
        {
            launcher.Open(url);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException or FileNotFoundException)
        {
            logger.LogWarning("Could not open the browser: {Error}", exception.Message);
        }
    }
}

public sealed record WebUiStartupOptions(bool OpenBrowser);
