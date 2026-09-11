using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class MovieWatchRepository(
    AppDbContext dbContext,
    ILogger<MovieWatchRepository> logger)
    : IMovieWatchRepository
{
    public async Task<DateTime?> GetWatchedUtcAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
    {
        return await dbContext.UserMovieWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.MovieTvdbId == movieTvdbId)
            .Select(x => (DateTime?)x.WatchedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ToggleAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.UserMovieWatches
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MovieTvdbId == movieTvdbId, cancellationToken);

        if (existing is not null)
        {
            dbContext.UserMovieWatches.Remove(existing);
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        dbContext.UserMovieWatches.Add(new UserMovieWatchEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MovieTvdbId = movieTvdbId,
            WatchedUtc = DateTime.UtcNow
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request already inserted the same watch mark — that's
            // fine, the end state is still "watched".
            logger.LogInformation(
                "Movie watch already exists for user {UserId}, movie {MovieTvdbId}.",
                userId, movieTvdbId);
        }

        return true;
    }

    public async Task<IReadOnlyList<MovieWatch>> GetWatchedMoviesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.UserMovieWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.WatchedUtc)
            .Select(x => new MovieWatch(x.MovieTvdbId, x.WatchedUtc))
            .ToListAsync(cancellationToken);
    }
}
