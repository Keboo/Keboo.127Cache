using Keboo.LocalCache.Ui;
using Microsoft.Extensions.Options;

namespace Keboo.LocalCache;

internal sealed class CatalogWarmupService(OverviewLoader overview, PackageCatalog catalog, IOptions<ProxyOptions> options) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        overview.Load(refresh: true);
        catalog.SetStartup(overview.Completion);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RefreshSeconds), options.Value.TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                overview.Load(refresh: true);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}