using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services.Sitemap;

namespace Recall.Web.Pages;

/// <summary>
/// Dynamic sitemap.xml — always includes the handful of genuinely static public
/// pages, plus a &lt;url&gt; for every series, movie and episode currently in the
/// local TheTVDB cache (i.e. everything Recall has ever looked up and could show
/// a details page for). Never requires sign-in; nothing here reads user data.
/// </summary>
public sealed class SitemapModel(ISitemapService sitemapService, ILogger<SitemapModel> logger) : PageModel
{
    private const string SiteUrl = "https://recall.nu";
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var urls = new List<XElement>
        {
            BuildUrl("/", changeFreq: "daily", priority: "1.0"),
            BuildUrl("/Account/Login", changeFreq: "monthly", priority: "0.5"),
            BuildUrl("/Privacy", changeFreq: "yearly", priority: "0.3")
        };

        try
        {
            var seriesTask = sitemapService.GetCachedSeriesAsync(cancellationToken);
            var moviesTask = sitemapService.GetCachedMoviesAsync(cancellationToken);
            var episodesTask = sitemapService.GetCachedEpisodesAsync(cancellationToken);

            await Task.WhenAll(seriesTask, moviesTask, episodesTask);

            urls.AddRange(seriesTask.Result.Select(e =>
                BuildUrl($"/Series/Details/{e.TvdbId}", e.LastModifiedUtc, "weekly", "0.7")));
            urls.AddRange(moviesTask.Result.Select(e =>
                BuildUrl($"/Movies/Details/{e.TvdbId}", e.LastModifiedUtc, "monthly", "0.7")));
            urls.AddRange(episodesTask.Result.Select(e =>
                BuildUrl($"/Episodes/Details/{e.TvdbId}", e.LastModifiedUtc, "monthly", "0.5")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken sitemap is worse than an incomplete one — serve the static
            // pages rather than a 500, and let the next crawl pick up the rest.
            logger.LogError(ex, "Failed to load cached content for the sitemap; serving static pages only.");
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "urlset", urls));

        // XDocument.ToString() omits the declaration by design — prepend it manually.
        var xml = document.Declaration + Environment.NewLine + document.Root;

        return Content(xml, "application/xml", Encoding.UTF8);
    }

    private static XElement BuildUrl(string path, DateTime? lastModifiedUtc = null, string? changeFreq = null, string? priority = null)
    {
        var url = new XElement(Ns + "url", new XElement(Ns + "loc", SiteUrl + path));

        if (lastModifiedUtc is { } lastMod)
            url.Add(new XElement(Ns + "lastmod", lastMod.ToString("yyyy-MM-dd")));

        if (changeFreq is not null)
            url.Add(new XElement(Ns + "changefreq", changeFreq));

        if (priority is not null)
            url.Add(new XElement(Ns + "priority", priority));

        return url;
    }
}
