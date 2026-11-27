using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services.Metrics;

namespace Recall.Web.Middleware;

/// <summary>
/// Counts page views for the site metrics: a GET of a Razor page that ended
/// in a 200 with an HTML body, by page type and whether the visitor was
/// signed in, or as a crawler's view. Static files, JSON handlers, the sitemap,
/// redirects and error pages are not views. Nothing about the request is kept
/// but the count (<see cref="SiteMetricKeys"/>). After authentication in the
/// pipeline, so it knows who is signed in.
/// </summary>
public sealed class PageViewMetricsMiddleware(RequestDelegate next, ISiteMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (!HttpMethods.IsGet(context.Request.Method)
            || context.Response.StatusCode != StatusCodes.Status200OK
            || context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true
            || context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not { } page)
        {
            return;
        }

        if (CrawlerUserAgents.IsCrawler(context.Request.Headers.UserAgent))
        {
            metrics.Increment(SiteMetricKeys.CrawlerView);
            return;
        }

        metrics.Increment(SiteMetricKeys.PageView(PageOf(page.ViewEnginePath), context.User.Identity?.IsAuthenticated == true));
    }

    /// <summary>"/Series/Details" is a series page; anything not named is <see cref="SitePage.Other"/>.</summary>
    public static SitePage PageOf(string? viewEnginePath) => viewEnginePath switch
    {
        "/Index" => SitePage.Landing,
        "/Series/Details" => SitePage.Series,
        "/Movies/Details" => SitePage.Movie,
        "/Episodes/Details" => SitePage.Episode,
        "/Discover" => SitePage.Discover,
        "/Search" => SitePage.Search,
        "/Account/Login" => SitePage.Login,
        _ => SitePage.Other
    };
}
