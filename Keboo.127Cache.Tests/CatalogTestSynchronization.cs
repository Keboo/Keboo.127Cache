using System.Net;
using System.Runtime.ExceptionServices;

namespace Keboo._127Cache.Testing;

internal static class CatalogTestSynchronization
{
    private static bool IsPending(string message) =>
        message.Contains("Build content is still being prepared", StringComparison.Ordinal)
        || message.Contains("Cached content verification is still in progress", StringComparison.Ordinal)
        || message.Contains("GitHub discovery is still in progress", StringComparison.Ordinal)
        || message.Contains("GitHub discovery is in progress", StringComparison.Ordinal);

    public static async Task<T> WhenSettledAsync<T>(PackageCatalog catalog, Func<Task<T>> request)
    {
        var started = catalog.BackgroundWorkStarted;
        T? result = default;
        DiscoveryUnavailableException? unavailable = null;
        try { result = await request(); }
        catch (DiscoveryUnavailableException exception) when (IsPending(exception.Message)
            || exception.Message.Contains("No usable cached packages", StringComparison.Ordinal))
        {
            unavailable = exception;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waited = await catalog.WaitForBackgroundWorkAsync(timeout.Token);
        if (unavailable is not null && !waited && !IsPending(unavailable.Message))
            ExceptionDispatchInfo.Capture(unavailable).Throw();
        return unavailable is not null || waited || catalog.BackgroundWorkStarted != started ? await request() : result!;
    }

    public static async Task<HttpResponseMessage> GetWhenSettledAsync(
        HttpClient client, PackageCatalog catalog, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var repeatedSuccess = false;
        var racedCompletion = false;
        for (;;)
        {
            var response = await client.GetAsync(path, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var waited = await catalog.WaitForBackgroundWorkAsync(timeout.Token);
            var pendingResponse = response.StatusCode == HttpStatusCode.ServiceUnavailable
                && (IsPending(body) || (waited && body.Contains("No usable cached packages", StringComparison.Ordinal)));
            if (pendingResponse && (waited || !racedCompletion))
            {
                racedCompletion = !waited;
                response.Dispose();
                continue;
            }
            if (response.IsSuccessStatusCode && waited && !repeatedSuccess)
            {
                repeatedSuccess = true;
                response.Dispose();
                continue;
            }
            return response;
        }
    }

    public static async Task<string> GetStringWhenSettledAsync(
        HttpClient client, PackageCatalog catalog, string path)
    {
        using var response = await GetWhenSettledAsync(client, catalog, path);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Feed returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
        return body;
    }
}
