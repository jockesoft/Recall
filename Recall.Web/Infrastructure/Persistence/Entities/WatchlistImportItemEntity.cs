namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>Outcome of processing a single <see cref="WatchlistImportItemEntity"/>.</summary>
public enum WatchlistImportItemStatus
{
    /// <summary>Not yet picked up by <c>WatchlistImportTimer</c>.</summary>
    Pending = 1,
    Imported = 2,
    AlreadyInLibrary = 3,
    NotFound = 4,

    /// <summary>A CSV "Title Type" Recall doesn't import (e.g. TV Episode, Short, Video Game).</summary>
    Unsupported = 5,
    Failed = 6
}

/// <summary>
/// A single row from an imported IMDb CSV, tracked from upload through
/// resolution against TheTVDB.
/// </summary>
public sealed class WatchlistImportItemEntity
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }
    public WatchlistImportJobEntity Job { get; set; } = null!;

    /// <summary>1-based position in the source CSV, for display ordering.</summary>
    public int RowNumber { get; set; }

    /// <summary>IMDb id, e.g. "tt2479478".</summary>
    public string ImdbId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Raw IMDb "Title Type" column value (e.g. "Movie", "TV Series").</summary>
    public string TitleType { get; set; } = string.Empty;

    /// <summary>The CSV's "Your Rating" column, 1-10, or null if the row was unrated.</summary>
    public int? YourRating { get; set; }

    public WatchlistImportItemStatus Status { get; set; } = WatchlistImportItemStatus.Pending;

    /// <summary>TVDB id this row resolved to, once known.</summary>
    public int? ResolvedTvdbId { get; set; }

    /// <summary>Short human-readable outcome, e.g. "Added to library" or "No TheTVDB match.".</summary>
    public string? ResultMessage { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
}
