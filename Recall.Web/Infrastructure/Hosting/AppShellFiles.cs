using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Recall.Web.Infrastructure.Hosting;

/// <summary>
/// The two files that make Recall an installable web app: the manifest and the
/// service worker. Both are fixed paths at the site root (a service worker's
/// scope is the folder it is served from, and the browser looks for a new
/// version at the same address), so they cannot be fingerprinted like the
/// stylesheets. They are sent with <c>Cache-Control: no-cache</c> instead:
/// the browser may keep a copy but must ask before using it, so a changed
/// worker or manifest takes effect on the next visit.
/// </summary>
public static class AppShellFiles
{
    public const string ServiceWorkerPath = "/sw.js";
    public const string ManifestPath = "/manifest.webmanifest";

    /// <summary>For <c>StaticFileOptions.OnPrepareResponse</c>.</summary>
    public static void PrepareResponse(StaticFileResponseContext context)
    {
        var path = context.Context.Request.Path;
        if (path.Equals(ServiceWorkerPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        }
    }
}
