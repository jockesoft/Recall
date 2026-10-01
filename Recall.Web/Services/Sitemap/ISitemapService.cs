namespace Recall.Web.Services.Sitemap;

/// <summary>
/// Reads TheTVDB id + last-refreshed timestamp straight from the local cache
/// tables, for building the public sitemap. Deliberately independent of
/// <c>ITvdbSnapshotStore</c> — this only needs id/timestamp pairs, not the full
/// cached payload, and has nothing to do with the read-through caching it owns.
/// </summary>
public interface ISitemapService
{
    /// <summary>
    /// Everything the sitemap lists from the cache, trimmed to
    /// <paramref name="maxEntries"/> in total. Series are filled first, then
    /// movies, then episodes with whatever room is left — the pages that matter
    /// most for search are never crowded out by the far more numerous episodes.
    /// Within each kind the most recently refreshed come first.
    /// </summary>
    Task<SitemapContent> GetCachedContentAsync(int maxEntries, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> distinct cached series, most recently refreshed first.</summary>
    Task<IReadOnlyList<SitemapEntry>> GetCachedSeriesAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> distinct cached movies, most recently refreshed first.</summary>
    Task<IReadOnlyList<SitemapEntry>> GetCachedMoviesAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> cached episodes, most recently refreshed first.</summary>
    Task<IReadOnlyList<SitemapEntry>> GetCachedEpisodesAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>The cached pages chosen for one sitemap, already within its size limit.</summary>
public sealed record SitemapContent(
    IReadOnlyList<SitemapEntry> Series,
    IReadOnlyList<SitemapEntry> Movies,
    IReadOnlyList<SitemapEntry> Episodes)
{
    public int Count => Series.Count + Movies.Count + Episodes.Count;
}

/// <summary>One cached item's id and last-refresh time, for a single sitemap <c>&lt;url&gt;</c> entry.</summary>
public sealed record SitemapEntry(int TvdbId, DateTime LastModifiedUtc);
