using Microsoft.Extensions.Options;

namespace Keboo._127Cache;

public static class ProxyExtensions
{
    public static IServiceCollection AddProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProxyOptions>()
            .Bind(configuration.GetSection("Proxy"))
            .Validate(options => options.IsValid(), "Invalid proxy configuration.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<ProxyOptions>>().Value);
        services.AddMemoryCache();
        services.AddSingleton<IGitHubClient, GitHubClient>();
        services.AddSingleton<ArtifactCache>();
        services.AddSingleton<PackageCatalog>();
        services.AddSingleton<PackageCache>();
        services.AddHostedService<CatalogWarmupService>();
        services.AddExceptionHandler<DiscoveryExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }

    public static void MapProxy(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ProxyOptions>();
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

        app.MapGet("/health", (PackageCatalog catalog) => Results.Ok(catalog.GetHealth()));
        app.MapGet("/builds", async (PackageCatalog catalog, CancellationToken ct) =>
        {
            var builds = await catalog.GetBuildsAsync(ct);
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

    private static bool IsFeedPath(PathString path) =>
        path.StartsWithSegments("/v3") || path.StartsWithSegments("/flat")
        || path.StartsWithSegments("/registration") || path.StartsWithSegments("/query")
        || path.StartsWithSegments("/autocomplete") || path.StartsWithSegments("/builds");

}