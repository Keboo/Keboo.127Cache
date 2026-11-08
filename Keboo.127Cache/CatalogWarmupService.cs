namespace Keboo._127Cache;

public sealed class CatalogWarmupService(PackageCatalog catalog, ILogger<CatalogWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var runs = await catalog.GetRunArtifactsAsync(stoppingToken);
            var health = catalog.GetHealth();
            if (health.Status == "degraded")
                logger.LogWarning("Catalog warmup is using a stale snapshot: {Error}", health.Error);
            else
                logger.LogInformation("Cached run and artifact metadata for {Count} completed PR builds.", runs.Length);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = catalog.ReportWarmupFailure(exception);
            logger.LogWarning("Catalog warmup failed: {Error}", error);
        }
    }
}