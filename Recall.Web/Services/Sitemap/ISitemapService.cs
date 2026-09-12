namespace Recall.Web.Services.Sitemap;

/// <summary>
/// Reads TheTVDB id + last-refreshed timestamp straight from the local cache
/// tables, for building the public sitemap. Deliberately independent of
/// <c>ITvdbSnapshotStore</c> — this only needs id/timestamp pairs, not the full
/// cached payload, and has nothing to do with the read-through caching it owns.
/// </summary>
public interface ISitemapService
{
    /// <summary>Every distinct series TVDB id currently cached, with its most recent refresh time.</summary>
    Task<IReadOnlyList<SitemapEntry>> GetCachedSeriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every distinct movie TVDB id currently cached, with its most recent refresh time.</summary>
    Task<IReadOnlyList<SitemapEntry>> GetCachedMoviesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every episode TVDB id currently cached, with its most recent refresh time.</summary>
    Task<IReadOnlyList<SitemapEntry>> GetCachedEpisodesAsync(CancellationToken cancellationToken = default);
}

/// <summary>One cached item's id and last-refresh time, for a single sitemap <c>&lt;url&gt;</c> entry.</summary>
public sealed record SitemapEntry(int TvdbId, DateTime LastModifiedUtc);
