namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>Lifecycle of a <see cref="WatchlistImportJobEntity"/>.</summary>
public enum WatchlistImportJobStatus
{
    Processing = 1,
    Completed = 2
}

/// <summary>
/// One CSV upload from a user (an IMDb "Ratings" or "Watchlist" export). Rows are
/// fanned out into <see cref="WatchlistImportItemEntity"/> immediately on upload;
/// this row just tracks aggregate progress so the UI can show "214/600" without
/// scanning the items table.
/// </summary>
public sealed class WatchlistImportJobEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;

    public WatchlistImportJobStatus Status { get; set; } = WatchlistImportJobStatus.Processing;

    public int TotalCount { get; set; }
    public int ProcessedCount { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public int NotFoundCount { get; set; }
    public int FailedCount { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public ICollection<WatchlistImportItemEntity> Items { get; set; } = new List<WatchlistImportItemEntity>();
}
