using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class TrackedMovieRepository(
    AppDbContext dbContext,
    ILogger<TrackedMovieRepository> logger)
    : ITrackedMovieRepository
{
    private const int MaxNameLength = 500;

    public Task<bool> ExistsAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
    {
        return dbContext.TrackedMovies
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.TvdbId == movieTvdbId, cancellationToken);
    }

    public async Task<IReadOnlyList<TrackedMovie>> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.TrackedMovies
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedUtc)
            .ThenBy(x => x.TvdbId)
            .Select(x => new TrackedMovie(x.TvdbId, x.Name, x.CreatedUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddAsync(Guid userId, int movieTvdbId, string name, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(userId, movieTvdbId, cancellationToken))
            return false;

        var trimmed = name.Trim();
        var entity = new TrackedMovieEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TvdbId = movieTvdbId,
            Name = trimmed.Length <= MaxNameLength ? trimmed : trimmed[..MaxNameLength]
        };

        dbContext.TrackedMovies.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request added the same movie between our check and
            // insert — the end state is still "on the watchlist".
            dbContext.Entry(entity).State = EntityState.Detached;
            logger.LogInformation(
                "Tracked movie already exists for user {UserId}, movie {MovieTvdbId}.", userId, movieTvdbId);
            return false;
        }
    }

    public async Task<bool> RemoveAsync(Guid userId, int movieTvdbId, CancellationToken cancellationToken = default)
    {
        var removed = await dbContext.TrackedMovies
            .Where(x => x.UserId == userId && x.TvdbId == movieTvdbId)
            .ExecuteDeleteAsync(cancellationToken);

        return removed > 0;
    }
}
