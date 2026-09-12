using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.Import;

/// <summary>
/// Bulk-imports an IMDb list export (Ratings or Watchlist CSV) into a user's
/// library/watch history. Resolving each row against TheTVDB is rate-limited,
/// so uploading only queues the rows — <see cref="ProcessNextBatchAsync"/> is
/// what actually does the work, called at a steady pace by
/// <c>WatchlistImportTimer</c>.
/// </summary>
public interface IWatchlistImportService
{
    /// <summary>
    /// Parses <paramref name="csvStream"/> and queues its rows for import.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The user already has an import running, the file isn't a recognizable
    /// IMDb export, or it has no usable rows.
    /// </exception>
    Task<WatchlistImportJob> StartImportAsync(
        Guid userId,
        Stream csvStream,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims and resolves up to <paramref name="batchSize"/> pending rows across
    /// every user's queue, oldest first. Called by <c>WatchlistImportTimer</c>.
    /// </summary>
    Task ProcessNextBatchAsync(int batchSize, CancellationToken cancellationToken = default);
}
