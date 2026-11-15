using System.Globalization;
using System.Text.RegularExpressions;

namespace Keboo.LocalCache.Ui;

internal static partial class UiFormat
{
    public static string FeedUrl(ProxyOptions options) => options.BaseUrl.TrimEnd('/') + "/v3/index.json";

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Age(DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is null) return "n/a";
        var age = now - since.Value;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    public static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

    public static string Reappearance(ReappearanceOutcome outcome) => outcome switch
    {
        ReappearanceOutcome.WillReappear => "pull request is open: downloaded again on the next refresh",
        ReappearanceOutcome.PullRequestClosed => "pull request is closed: cannot be restored",
        ReappearanceOutcome.ArtifactExpired => "artifacts expired on GitHub: cannot be restored",
        ReappearanceOutcome.SupersededByNewerRun => "a newer run of the pull request is served: this run cannot be restored",
        ReappearanceOutcome.NotAvailableUpstream => "no longer available on GitHub: cannot be restored",
        _ => "GitHub could not be queried: it may not be restorable"
    };

    public static string CacheState(BuildInfo build) => build.CachedState switch
    {
        "cached" => "Downloaded",
        "partial" => "Partially downloaded",
        _ => "Metadata only"
    };

    // Links are only produced for values that look like a GitHub "owner/name" repository.
    public static string? RepositoryUrl(string repository) =>
        RepositoryName().IsMatch(repository) ? "https://github.com/" + repository : null;

    public static string? PullRequestUrl(string repository, int pullRequest) =>
        RepositoryUrl(repository) is { } url ? $"{url}/pull/{pullRequest}" : null;

    public static string? RunUrl(string repository, long runId, int attempt) =>
        RepositoryUrl(repository) is { } url ? $"{url}/actions/runs/{runId}/attempts/{attempt}" : null;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();
}
