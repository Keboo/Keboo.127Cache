using Microsoft.Extensions.Options;

namespace Keboo._127Cache;

public static class ProxyExtensions
{
    public static IServiceCollection AddProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();
        services.AddSingleton(provider =>
            CacheConfigurationLoader.Load(configuration, provider.GetRequiredService<ILogger<CacheConfiguration>>()));
        services.AddSingleton(provider => ProxyOptions.FromConfiguration(provider.GetRequiredService<CacheConfiguration>()));
        services.AddSingleton<IOptions<ProxyOptions>>(provider => Options.Create(provider.GetRequiredService<ProxyOptions>()));
        services.AddMemoryCache();
        services.AddSingleton<IGitHubClient, GitHubClient>();
        services.AddSingleton<ArtifactCache>();
        services.AddSingleton<PackageCatalog>();
        services.AddSingleton<PackageCache>();
        services.AddHostedService<CatalogWarmupService>();
        services.AddProblemDetails();
        return services;
    }

    public static void MapProxy(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ProxyOptions>();
        app.MapGet("/health", () => Results.Ok(new { status = "ok", repository = options.Repository }));
        app.MapGet("/builds", async (PackageCatalog catalog, CancellationToken ct) => Results.Ok(await catalog.GetScriptBuildsAsync(ct)));
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
}