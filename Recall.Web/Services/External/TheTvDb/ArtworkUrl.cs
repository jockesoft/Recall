namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// TheTVDB hands back artwork paths in a few shapes: site-relative
/// ("/banners/v4/episode/.../screencap/...jpg") from some endpoints/fields,
/// and fully-qualified URLs from others. Which shape a given field returns
/// isn't reliable per-field or per-endpoint, so every raw path from TheTVDB
/// must be normalized before it's used as an <c>&lt;img src&gt;</c> — an
/// unnormalized relative path resolves against the current page's own
/// origin and 404s instead of rendering.
/// </summary>
public static class ArtworkUrl
{
    private const string BaseUrl = "https://artworks.thetvdb.com";

    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim();

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{BaseUrl}/{url.TrimStart('/')}";
    }
}
