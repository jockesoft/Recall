namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IMovieWatchRepository
{
    /// <summary>When the given user marked the given movie watched, or <c>null</c> if they haven't.</summary>
    Task<DateTime?> GetWatchedUtcAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the movie watched if not already, or un-watched if it was. Returns
    /// the resulting state: <c>true</c> when now watched, <c>false</c> when just
    /// un-marked. Safe against a concurrent double-submit.
    /// </summary>
    Task<bool> ToggleAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default);

    /// <summary>Every movie the user has marked watched, newest first.</summary>
    Task<IReadOnlyList<MovieWatch>> GetWatchedMoviesAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>A single movie watch mark, flattened for read use.</summary>
public sealed record MovieWatch(int MovieTvdbId, DateTime WatchedUtc);
