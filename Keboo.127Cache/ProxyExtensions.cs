using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Keboo.LocalCache.Ui;

namespace Keboo.LocalCache;

public static class ProxyExtensions
{
    public static IServiceCollection AddProxy(this IServiceCollection services, IConfiguration configuration,
        SourceConfigurationLocations? locations = null)
    {
        services.AddLogging();
        services.TryAddSingleton(locations ?? SourceConfigurationLocations.None);
        services.AddSingleton(provider =>
            CacheConfigurationLoader.Load(configuration, provider.GetRequiredService<ILogger<CacheConfiguration>>()));
        services.AddOptions<ProxyOptions>()
            .Configure<CacheConfiguration>((options, configuration) => options.ConfigureFromConfiguration(configuration));
        services.AddMemoryCache();
        services.AddSingleton<IBuildSourceFactory, DefaultBuildSourceFactory>();
        services.AddSingleton(provider => PackageCatalog.Create(
            provider.GetRequiredService<ProxyOptions>(), provider.GetRequiredService<IBuildSourceFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            provider.GetRequiredService<ILogger<PackageCatalog>>(), provider.GetService<ILogger<ArtifactCache>>(),
            provider.GetService<ILogger<PackageCache>>(),
            provider.GetService<IHostApplicationLifetime>()));
        services.AddHostedService<CatalogWarmupService>();
        services.AddSingleton<ICacheEvictionService, CacheEvictionService>();
        services.AddSingleton<IManagementService, ManagementService>();
        services.AddSingleton(provider => new SourceConfigurationService(configuration,
            provider.GetRequiredService<SourceConfigurationLocations>(), provider.GetRequiredService<PackageCatalog>(),
            provider.GetRequiredService<ProxyOptions>(), provider.GetRequiredService<ICacheEvictionService>(),
            provider.GetRequiredService<ILogger<CacheConfiguration>>()));
        services.AddSingleton<ISourceConfigurationService>(provider => provider.GetRequiredService<SourceConfigurationService>());
        services.AddHostedService(provider => provider.GetRequiredService<SourceConfigurationService>());
        services.AddHostedService<CacheRetentionService>();
        services.AddExceptionHandler<DiscoveryExceptionHandler>();
        services.AddProblemDetails();
        services.AddWebUi();
        return services;
    }

    public static void MapProxy(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;
        app.Use(async (context, next) =>
        {
            if (!IsFeedPath(context.Request.Path))
            {
                await next(context);
                return;
            }

            var catalog = context.RequestServices.GetRequiredService<PackageCatalog>();
            using var scope = catalog.BeginFeedRequest();
            await catalog.EnsureFeedAvailableAsync(context.RequestAborted);
            var getAvailability = catalog.CaptureRequestAvailability();
            context.Response.OnStarting(() =>
            {
                var current = getAvailability();
                if (current.IsDegraded)
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["X-127Cache-Status"] = current.IsPartial ? "degraded-partial" : "degraded";
                    context.Response.Headers["X-127Cache-Membership"] = current.Membership;
                    context.Response.Headers["X-127Cache-Provenance"] = current.Provenance;
                    context.Response.Headers.Warning = "110 - \"Response is stale\"";
                }
                return Task.CompletedTask;
            });
            try
            {
                await next(context);
            }
            catch (Exception exception) when (exception is not DiscoveryUnavailableException && !context.Response.HasStarted
                && DiscoveryFailure.CanHandleContent(exception, context.RequestAborted))
            {
                throw new DiscoveryUnavailableException(catalog.ReportLateContentFailure(exception));
            }
        });

        ManagementEndpoints.Map(app);
        app.MapWebUi();
        app.MapGet("/health", (PackageCatalog catalog) => Results.Ok(catalog.GetHealth()));
        app.MapGet("/builds", async (PackageCatalog catalog, CancellationToken ct) =>
        {
            var builds = await catalog.GetScriptBuildsAsync(ct);
            catalog.EnsureAggregateComplete();
            return Results.Ok(builds);
        });
        app.MapMethods("/v3/index.json", ["GET", "HEAD"], () => Results.Json(new
        {
            version = "3.0.0",
            resources = new object[] {
                FeedEndpoints.Resource(options.BaseUrl + "/flat/", "PackageBaseAddress/3.0.0"),
                FeedEndpoints.Resource(options.BaseUrl + "/registration/", "RegistrationsBaseUrl/3.6.0"),
                FeedEndpoints.Resource(options.BaseUrl + "/query", "SearchQueryService/3.0.0-beta"),
                FeedEndpoints.Resource(options.BaseUrl + "/query", "SearchQueryService/3.5.0"),
                FeedEndpoints.Resource(options.BaseUrl + "/autocomplete", "SearchAutocompleteService/3.0.0-beta"),
                FeedEndpoints.Resource(options.BaseUrl + "/autocomplete", "SearchAutocompleteService/3.5.0")
            }
        }));
        FeedEndpoints.Map(app);
    }

    internal static bool IsFeedPath(PathString path) =>
        path.StartsWithSegments("/v3") || path.StartsWithSegments("/flat")
        || path.StartsWithSegments("/registration") || path.StartsWithSegments("/query")
        || path.StartsWithSegments("/autocomplete") || path.StartsWithSegments("/builds");

}