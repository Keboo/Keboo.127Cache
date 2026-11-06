using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Diagnostics;

namespace Keboo._127Cache;

public sealed class DiscoveryUnavailableException(string message) : IOException(message);
internal sealed class CacheMetadataUnavailableException : IOException;
internal sealed class SnapshotPersistenceException : IOException;

internal static class DiscoveryFailure
{
    public static bool CanHandle(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    public static string Describe(Exception exception)
    {
        if (exception is CacheMetadataUnavailableException)
            return "Cached catalog or run metadata cannot be read. Restore it or choose a different cache directory; the original files were preserved.";
        if (exception is SnapshotPersistenceException)
            return "Could not save discovery metadata. Check the cache directory permissions and available space.";
        if (exception is Win32Exception)
            return "GitHub CLI 'gh' could not be started. Install it from https://cli.github.com/ and run 'gh auth login'.";
        if (exception is OperationCanceledException or TimeoutException)
            return "GitHub discovery timed out. Check your network connection and try again.";
        if (exception is InvalidDataException && Regex.IsMatch(exception.Message,
            @"^Run \d+ has (expired artifacts that are not cached locally|missing package/version artifacts)\.$"))
            return exception.Message;
        if (exception is JsonException or InvalidDataException)
            return "GitHub returned invalid or unavailable build metadata. Check repository access and try again.";

        var message = exception.Message;
        if (message.Contains("GitHub CLI failed (4)", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not logged", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bad credentials", StringComparison.OrdinalIgnoreCase)
            || message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
            return "GitHub CLI authentication failed. Run 'gh auth login' and 'gh auth status', and check repository access.";

        return "GitHub discovery failed. Check your network connection and repository access, and run 'gh auth status'.";
    }

    public static string DescribeContent(Exception exception)
    {
        if (exception is Win32Exception or OperationCanceledException or TimeoutException
            || exception.Message.Contains("GitHub CLI failed (4)", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("not logged", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("bad credentials", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
            return Describe(exception);
        if (exception is InvalidDataException or JsonException or FormatException)
            return "Cached build content is incomplete or invalid. Restore the affected cache files or retry when GitHub is available.";
        return "A build artifact is unavailable. Check the network and repository access, and run 'gh auth status'.";
    }

    public static bool CanHandleContent(Exception exception, CancellationToken cancellationToken) =>
        CanHandle(exception, cancellationToken)
        && exception is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException
            or FormatException or TimeoutException or OperationCanceledException;
}

internal sealed class DiscoveryExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DiscoveryUnavailableException) return false;

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "5";
        context.Response.Headers["X-127Cache-Status"] = "degraded";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = "The cached feed is unavailable",
            status = StatusCodes.Status503ServiceUnavailable,
            detail = exception.Message
        }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}