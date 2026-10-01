namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Owns a user's relationship with a movie: on the watchlist ("want to
/// watch") or watched, never both. Page models and the importer call this
/// rather than the two repositories, so that rule lives in one place.
/// </summary>
public interface IMovieTrackingService
{
    Task<bool> IsOnWatchlistAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a movie on the user's watchlist. Pass <paramref name="knownName"/>
    /// when the caller has already resolved the movie (the importer has);
    /// otherwise the movie is looked up first, which also confirms it exists.
    /// </summary>
    Task<MovieWatchlistOutcome> AddToWatchlistAsync(
        Guid userId,
        int movieTvdbId,
        string? knownName = null,
        CancellationToken cancellationToken = default);

    /// <summary>Takes a movie off the watchlist. Returns <c>false</c> when it wasn't on it.</summary>
    Task<bool> RemoveFromWatchlistAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flips the movie's watched state and returns the result (<c>true</c> =
    /// now watched). Marking it watched takes it off the watchlist; un-marking
    /// does not put it back.
    /// </summary>
    Task<bool> ToggleWatchedAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes sure the movie is marked watched (and off the watchlist) without
    /// ever un-marking it. Returns <c>true</c> when it was already watched.
    /// </summary>
    Task<bool> MarkWatchedAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);
}

public enum MovieWatchlistOutcome
{
    Added,
    AlreadyOnWatchlist,

    /// <summary>The user has already watched it — nothing was added.</summary>
    AlreadyWatched,

    /// <summary>TheTVDB has no such movie — nothing was added.</summary>
    MovieNotFound
}
