using System.Threading.Channels;

namespace Keboo.LocalCache.Ui;

internal sealed record OverviewState
{
    public long Revision { get; init; }
    public bool Busy { get; init; }
    public string Status { get; init; } = "Loading overview…";
    public IReadOnlyList<SourceInfo>? Sources { get; init; }
    public IReadOnlyList<BuildInfo>? Builds { get; init; }
    public IReadOnlyDictionary<(string Source, int PullRequest, long RunId, int Attempt), IReadOnlyList<string>> PackageIds { get; init; }
        = new Dictionary<(string, int, long, int), IReadOnlyList<string>>();
    public long? DiskBytes { get; init; }
    public string? Error { get; init; }
    public RefreshSummary? Refresh { get; init; }
}

// One host-owned operation, not one download per browser. The channel keeps even synchronous disk work off HTTP threads.
internal sealed class OverviewLoader(IManagementService management, ICacheEvictionService eviction,
    ILogger<OverviewLoader> logger) : BackgroundService
{
    private sealed record Request(bool Refresh, string? Source, TaskCompletionSource Completion);
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly object _gate = new();
    private OverviewState _state = new();
    private Task _completion = Task.CompletedTask;

    public OverviewState State { get { lock (_gate) return _state; } }
    internal Task Completion { get { lock (_gate) return _completion; } }

    public void Load(bool refresh = false, string? source = null)
    {
        lock (_gate)
        {
            if (_state.Busy) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion.Task;
            _state = _state with
            {
                Busy = true,
                Revision = _state.Revision + 1,
                Error = null,
                Refresh = null,
                Status = refresh ? "Discovering completed builds…" : "Loading cached metadata and discovering completed builds…"
            };
            if (!_requests.Writer.TryWrite(new Request(refresh, source, completion)))
            {
                _state = _state with { Busy = false, Status = "Overview loader stopped." };
                completion.TrySetResult();
            }
        }
    }

    private void Update(Func<OverviewState, OverviewState> update)
    {
        lock (_gate) _state = update(_state) with { Revision = _state.Revision + 1 };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // These sections publish separately, so a slow discovery never hides disk or source information.
                    await Task.WhenAll(ObserveAsync(() => LoadSourcesAsync(stoppingToken), stoppingToken),
                        ObserveAsync(() => LoadDiskAsync(stoppingToken), stoppingToken),
                        ObserveAsync(() => LoadBuildsAsync(request, stoppingToken), stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Overview background loading failed.");
                    Update(state => state with { Error = exception.Message });
                }
                finally
                {
                    Update(state => state with
                    {
                        Busy = false,
                        Status = state.Error is not null || state.Sources?.Any(x => x.Status != "ok") == true
                            ? "Overview finished with errors. Retry to update."
                            : state.Refresh is { Succeeded: false } ? "Refresh finished with errors."
                            : request.Refresh ? "Refresh finished." : "Overview ready."
                    });
                    request.Completion.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _requests.Writer.TryComplete();
            while (_requests.Reader.TryRead(out var pending)) pending.Completion.TrySetResult();
            Update(state => state with { Busy = false, Status = "Overview loader stopped." });
        }
    }

    private async Task ObserveAsync(Func<Task> load, CancellationToken token)
    {
        try { await load(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Overview section loading failed.");
            Update(state => state with
            {
                Error = state.Error is null ? exception.Message : $"{state.Error} {exception.Message}"
            });
        }
    }

    private async Task LoadSourcesAsync(CancellationToken token)
    {
        var sources = await management.GetSourcesAsync(token);
        Update(state => state with { Sources = sources });
    }

    private async Task LoadDiskAsync(CancellationToken token)
    {
        var usage = await eviction.GetDiskUsageAsync(cancellationToken: token);
        Update(state => state with { DiskBytes = usage.TotalBytes });
    }

    private async Task LoadBuildsAsync(Request request, CancellationToken token)
    {
        if (request.Refresh)
        {
            var result = await management.RefreshAsync(request.Source, token);
            Update(state => state with { Refresh = result });
        }
        await ReadBuildsAsync(token);
        if (request.Refresh)
        {
            Update(state => state with { Status = "Preparing packages (downloading and rewriting uncached builds)…" });
            await management.PrepareOverviewPackagesAsync(request.Source, token);
            await ReadBuildsAsync(token);
            await LoadDiskAsync(token);
        }
        await LoadSourcesAsync(token);
    }

    private async Task ReadBuildsAsync(CancellationToken token)
    {
        var builds = await management.GetOverviewBuildsAsync(token);
        Update(state => state with { Builds = builds });
        var packages = await management.GetCachedPackagesAsync(token);
        var ids = packages.SelectMany(package => package.Versions.Select(version => (package.Id, Version: version)))
            .GroupBy(x => (x.Version.Source, x.Version.PullRequest, x.Version.RunId, x.Version.Attempt))
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Update(state => state with { PackageIds = ids });
    }
}