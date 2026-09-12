using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IWatchlistImportRepository
{
    /// <summary>The user's currently-processing job, if any.</summary>
    Task<WatchlistImportJob?> GetActiveJobForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's most recently created import job (any status), or <c>null</c>
    /// if they've never imported. Pass <paramref name="includeItems"/> false for
    /// a summary-only read (e.g. the profile page's progress card).
    /// </summary>
    Task<WatchlistImportJob?> GetLatestJobForUserAsync(
        Guid userId, bool includeItems = true, CancellationToken cancellationToken = default);

    Task<WatchlistImportJob> CreateJobAsync(
        Guid userId,
        string fileName,
        IReadOnlyList<NewWatchlistImportItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> pending items, oldest job first,
    /// across every user's queue — a single fair FIFO rather than one queue per user.
    /// </summary>
    Task<IReadOnlyList<WatchlistImportItem>> ClaimNextPendingBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    Task MarkItemResultAsync(
        Guid itemId,
        WatchlistImportItemStatus status,
        int? resolvedTvdbId,
        string? resultMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes a job's aggregate counters from its items and flips it to
    /// <see cref="WatchlistImportJobStatus.Completed"/> once every item has been processed.
    /// </summary>
    Task RecalculateJobProgressAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>One parsed CSV row, queued for import.</summary>
public sealed record NewWatchlistImportItem(
    int RowNumber,
    string ImdbId,
    string Title,
    string TitleType,
    int? YourRating,
    bool IsSupported);

/// <summary>An import job's aggregate progress, flattened for read use.</summary>
public sealed record WatchlistImportJob(
    Guid Id,
    Guid UserId,
    string FileName,
    WatchlistImportJobStatus Status,
    int TotalCount,
    int ProcessedCount,
    int ImportedCount,
    int SkippedCount,
    int NotFoundCount,
    int FailedCount,
    DateTime CreatedUtc,
    DateTime? CompletedUtc,
    IReadOnlyList<WatchlistImportItem> Items);

/// <summary>A single import row, flattened for read use.</summary>
public sealed record WatchlistImportItem(
    Guid Id,
    Guid JobId,
    Guid UserId,
    int RowNumber,
    string ImdbId,
    string Title,
    string TitleType,
    int? YourRating,
    WatchlistImportItemStatus Status,
    int? ResolvedTvdbId,
    string? ResultMessage,
    DateTime CreatedUtc,
    DateTime? ProcessedUtc);
