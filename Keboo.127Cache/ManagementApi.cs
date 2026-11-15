using System.Net;

namespace Keboo.LocalCache;

public sealed record RemovedBuildResponse(
    string Source, int PullRequest, long RunId, int Attempt, long BytesReclaimed, bool Removed, string Reappearance,
    bool WillReappear, string? Error);

public sealed record RemovalResponse(
    bool DryRun, long BytesReclaimed, int RemovedCount, bool AnyWillReappear, bool AnyPermanent,
    IReadOnlyList<RemovedBuildResponse> Builds)
{
    public static RemovalResponse From(RemovalResult result) => new(
        result.DryRun, result.BytesReclaimed, result.RemovedCount, result.AnyWillReappear, result.AnyPermanent,
        result.Builds.Select(x => new RemovedBuildResponse(x.Source, x.PullRequest, x.RunId, x.Attempt, x.BytesReclaimed,
            x.Removed, x.Reappearance.ToString(), x.WillReappear, x.Error)).ToArray());
}

// Cross-site request protection for the management API. Any web page can send requests to a loopback server, so:
//  * every request must target a loopback or configured host name (defeats DNS rebinding);
//  * state-changing requests must not come from another origin (Origin / Sec-Fetch-Site) and must carry the custom
//    ClientHeader, which browsers only send cross-origin after a CORS preflight that this server never allows.
// Blazor form posts do not use this header: they post to the UI's own endpoints and rely on ASP.NET Core antiforgery
// (UseAntiforgery + the antiforgery token in the form), and can call ValidateSameOrigin for the Origin/Host part.
public static class ManagementRequestGuard
{
    public const string ClientHeader = "X-127Cache-Client";
    public const string ApiVersionHeader = "X-127Cache-Api-Version";

    // Returns why the request was rejected, or null when it is acceptable. Host and Origin only.
    public static string? ValidateSameOrigin(HttpRequest request, ProxyOptions options)
    {
        if (!IsAllowedHost(request.Host.Host, options))
            return $"The Host '{request.Host}' is not a loopback or configured host name.";
        if (IsSafe(request.Method)) return null;
        if (request.Headers.TryGetValue("Origin", out var origin) && origin.Count > 0)
        {
            var expected = new Uri($"{request.Scheme}://{request.Host}");
            if (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var actual)
                || Uri.Compare(expected, actual, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) != 0)
                return $"The request origin '{origin}' is not the origin of this server.";
        }
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site)
            && site.Any(x => !string.Equals(x, "same-origin", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x, "none", StringComparison.OrdinalIgnoreCase)))
            return "Cross-site requests are not allowed.";
        return null;
    }

    // ValidateSameOrigin plus the required custom header for JSON calls.
    public static string? Validate(HttpRequest request, ProxyOptions options)
    {
        var failure = ValidateSameOrigin(request, options);
        if (failure is not null || IsSafe(request.Method)) return failure;
        return request.Headers[ClientHeader].Any(x => !string.IsNullOrWhiteSpace(x))
            ? null : $"State-changing requests must send the '{ClientHeader}' header.";
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsAllowedHost(string host, ProxyOptions options)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) return true;
        return Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var configured)
            && string.Equals(configured.Host.Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ManagementEndpoints
{
    public static void Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<ProxyOptions>();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }
            context.Response.Headers[ManagementRequestGuard.ApiVersionHeader] = "1";
            var failure = ManagementRequestGuard.Validate(context.Request, options);
            if (failure is not null)
            {
                await Results.Problem(title: "Cross-site request rejected", detail: failure,
                    statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }
            await next(context);
        });

        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.Problem(title: "Not found", detail: exception.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (DiscoveryUnavailableException exception)
            {
                context.HttpContext.Response.Headers.RetryAfter = "5";
                return Results.Problem(title: "The cached feed is unavailable", detail: exception.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (ArgumentException exception)
            {
                return Results.Problem(title: "Invalid request", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapGet("/sources", async (IManagementService management, CancellationToken ct) =>
            Results.Ok(await management.GetSourcesAsync(ct)));
        api.MapGet("/builds", async (IManagementService management, string? source, CancellationToken ct) =>
            Results.Ok(await management.GetBuildsAsync(source, ct)));
        api.MapGet("/packages", async (IManagementService management, string? source, CancellationToken ct) =>
            Results.Ok(await management.GetPackagesAsync(source, ct)));
        api.MapPost("/refresh", async (IManagementService management, string? source, CancellationToken ct) =>
            Results.Ok(await management.RefreshAsync(source, ct)));

        api.MapDelete("/builds/{source}/{runId:long}/{attempt:int}", async (ICacheEvictionService eviction,
            string source, long runId, int attempt, bool? dryRun, CancellationToken ct) =>
            Removal(await eviction.RemoveBuildAsync(source, runId, attempt, dryRun == true, ct)));
        api.MapDelete("/pull-requests/{source}/{pullRequest:int}", async (ICacheEvictionService eviction,
            string source, int pullRequest, bool? dryRun, CancellationToken ct) =>
            Removal(await eviction.RemovePullRequestAsync(source, pullRequest, dryRun == true, ct)));
        api.MapDelete("/sources/{source}/cache", async (ICacheEvictionService eviction,
            string source, bool? dryRun, CancellationToken ct) =>
            Removal(await eviction.RemoveSourceAsync(source, dryRun == true, ct)));
        api.MapDelete("/closed-pull-requests", async (ICacheEvictionService eviction,
            string? source, double? closedForDays, bool? dryRun, CancellationToken ct) =>
        {
            if (closedForDays is < 0 or double.NaN or > 36500)
                throw new ArgumentException("closedForDays must be between 0 and 36500.");
            TimeSpan? closedFor = closedForDays is { } days ? TimeSpan.FromDays(days) : null;
            return Removal(await eviction.RemoveClosedPullRequestsAsync(source, closedFor, dryRun == true, ct));
        });

        api.Map("/{**path}", () => Results.Problem(title: "Not found", detail: "No such management API endpoint.",
            statusCode: StatusCodes.Status404NotFound));
    }

    private static IResult Removal(RemovalResult result)
    {
        var body = RemovalResponse.From(result);
        return body.Builds.Any(x => x.Error is not null)
            ? Results.Problem(title: "Some builds could not be removed",
                detail: "At least one build is in use; the remaining builds were removed or left untouched.",
                statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["removal"] = body })
            : Results.Ok(body);
    }
}
