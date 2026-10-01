namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>
/// The movie watchlist. Page models and the importer go through
/// <c>IMovieTrackingService</c> for writes, which also enforces that a watched
/// movie is not on the watchlist; this is only the storage.
/// </summary>
public interface ITrackedMovieRepository
{
    Task<bool> ExistsAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);

    /// <summary>Every movie on the user's watchlist, most recently added first.</summary>
    Task<IReadOnlyList<TrackedMovie>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the movie to the user's watchlist. Returns <c>false</c> when it was
    /// already there (including when a concurrent request added it first).
    /// </summary>
    Task<bool> AddAsync(Guid userId, int movieTvdbId, string name, CancellationToken cancellationToken = default);

    /// <summary>Removes the movie from the user's watchlist. Returns <c>false</c> when it wasn't on it.</summary>
    Task<bool> RemoveAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);
}

/// <summary>One watchlist entry, flattened for read use.</summary>
public sealed record TrackedMovie(int MovieTvdbId, string Name, DateTime AddedUtc);
