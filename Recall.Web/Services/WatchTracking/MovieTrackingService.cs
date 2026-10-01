using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.WatchTracking;

public sealed class MovieTrackingService(
    ITrackedMovieRepository trackedMovieRepository,
    IMovieWatchRepository movieWatchRepository,
    ITheTvDbService theTvDbService)
    : IMovieTrackingService
{
    // Every call below is sequential: both repositories share the request's
    // scoped AppDbContext, which allows one operation at a time.

    public Task<bool> IsOnWatchlistAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
        => trackedMovieRepository.ExistsAsync(userId, movieTvdbId, cancellationToken);

    public async Task<MovieWatchlistOutcome> AddToWatchlistAsync(
        Guid userId,
        int movieTvdbId,
        string? knownName = null,
        CancellationToken cancellationToken = default)
    {
        if (await movieWatchRepository.GetWatchedUtcAsync(userId, movieTvdbId, cancellationToken) is not null)
            return MovieWatchlistOutcome.AlreadyWatched;

        var name = knownName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var movie = await theTvDbService.GetMovieAggregateByIdAsync(movieTvdbId, cancellationToken);
            if (movie is null)
                return MovieWatchlistOutcome.MovieNotFound;

            name = movie.Name;
        }

        return await trackedMovieRepository.AddAsync(userId, movieTvdbId, name, cancellationToken)
            ? MovieWatchlistOutcome.Added
            : MovieWatchlistOutcome.AlreadyOnWatchlist;
    }

    public Task<bool> RemoveFromWatchlistAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
        => trackedMovieRepository.RemoveAsync(userId, movieTvdbId, cancellationToken);

    public async Task<bool> ToggleWatchedAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
    {
        var isNowWatched = await movieWatchRepository.ToggleAsync(userId, movieTvdbId, cancellationToken);

        if (isNowWatched)
            await trackedMovieRepository.RemoveAsync(userId, movieTvdbId, cancellationToken);

        return isNowWatched;
    }

    public async Task<bool> MarkWatchedAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
    {
        var alreadyWatched =
            await movieWatchRepository.GetWatchedUtcAsync(userId, movieTvdbId, cancellationToken) is not null;

        if (!alreadyWatched)
            await movieWatchRepository.ToggleAsync(userId, movieTvdbId, cancellationToken);

        // Also when it was already watched: heals a row left on the watchlist by
        // anything that marked the movie watched without going through here.
        await trackedMovieRepository.RemoveAsync(userId, movieTvdbId, cancellationToken);

        return alreadyWatched;
    }
}
