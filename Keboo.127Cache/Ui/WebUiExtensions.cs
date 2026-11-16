using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache.Ui;

public static class WebUiExtensions
{
    internal const string KeyRingDirectoryName = ".data-protection";

    // Blazor Web App with static server-side rendering only: no interactive render mode, no WebAssembly, no blazor.web.js.
    // Components read IManagementService / ICacheEvictionService directly instead of calling the JSON API.
    public static IServiceCollection AddWebUi(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddAntiforgery(options => options.Cookie.Name = ".Keboo.127Cache.Antiforgery");

        // Antiforgery tokens need a key ring, which ASP.NET Core would otherwise place under %LOCALAPPDATA% (or the
        // user's home directory). Keep it inside the cache directory so the app never writes outside its own data.
        services.AddDataProtection().SetApplicationName("Keboo.127Cache");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<ProxyOptions>, ILoggerFactory>((management, proxy, loggers) =>
        {
            var directory = new DirectoryInfo(Path.Combine(proxy.Value.CacheDirectory, KeyRingDirectoryName));
            management.XmlRepository = new FileSystemXmlRepository(directory, loggers);
        });
        // The key ring only protects loopback CSRF tokens, so the per-key 'unencrypted' warning is just noise.
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", LogLevel.Error));
        services.AddSingleton<IBrowserLauncher, ProcessBrowserLauncher>();
        return services;
    }

    public static IServiceCollection AddWebUiStartup(this IServiceCollection services, bool openBrowser)
    {
        services.AddSingleton(new WebUiStartupOptions(openBrowser));
        services.AddHostedService<WebUiStartup>();
        return services;
    }

    // Pages live at /, /packages and /sources. The feed (/v3, /flat, /registration, /query, /autocomplete), /api,
    // /health and the script-compatible /builds endpoint keep their routes; the UI never claims those paths.
    public static void MapWebUi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
        // DNS-rebinding and cross-origin protection for every UI request. Form posts additionally need the antiforgery
        // token that UseAntiforgery validates for endpoints that accept forms.
        app.Use(async (context, next) =>
        {
            if (IsUiPath(context.Request.Path)
                && ManagementRequestGuard.ValidateSameOrigin(context.Request, options) is { } failure)
            {
                await Results.Problem(title: "Request rejected", detail: failure, statusCode: StatusCodes.Status403Forbidden)
                    .ExecuteAsync(context);
                return;
            }
            await next(context);
        });
        app.UseAntiforgery();
        UiAssets.Map(app);
        app.MapRazorComponents<App>();
    }

    private static bool IsUiPath(PathString path) =>
        !ProxyExtensions.IsFeedPath(path) && !path.StartsWithSegments("/api") && !path.StartsWithSegments("/health");
}
