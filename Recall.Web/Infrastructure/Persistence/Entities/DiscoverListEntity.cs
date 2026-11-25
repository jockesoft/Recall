namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// One of Discover's lists, as the daily job last fetched it from TheTVDB's
/// filter endpoints ("series-popular-2026", "movie-genre-14"): the titles
/// that passed the quality filter, in TheTVDB's order, as JSON. Shared
/// metadata like the <c>cached_*</c> tables: it belongs to nobody, and a
/// Discover page view reads these rows and never TheTVDB.
/// </summary>
public sealed class DiscoverListEntity
{
    /// <summary>The list's name (<c>DiscoverListKey</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Serialized <c>List&lt;DiscoverTitle&gt;</c> (jsonb).</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>How many titles the payload holds.</summary>
    public int ItemCount { get; set; }

    /// <summary>When TheTVDB last answered for this list. A failed fetch leaves it, and the list, as they were.</summary>
    public DateTime FetchedUtc { get; set; }
}
