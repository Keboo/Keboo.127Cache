namespace Keboo.LocalCache.Ui;

// The few static assets of the UI are embedded in the assembly, so they ship inside the tool package and the browser
// never needs the network. They are served under /_ui, which no feed, API or page route uses.
internal static class UiAssets
{
    public const string StyleSheetPath = "/_ui/app.css";
    public const string ScriptPath = "/_ui/app.js";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        MapAsset(endpoints, StyleSheetPath, "app.css", "text/css; charset=utf-8");
        MapAsset(endpoints, ScriptPath, "app.js", "text/javascript; charset=utf-8");
    }

    private static void MapAsset(IEndpointRouteBuilder endpoints, string path, string name, string contentType)
    {
        var resource = typeof(UiAssets).Assembly.GetManifestResourceNames()
            .Single(x => x.EndsWith("." + name, StringComparison.Ordinal));
        endpoints.MapGet(path, () => Results.Stream(typeof(UiAssets).Assembly.GetManifestResourceStream(resource)!, contentType));
    }
}
