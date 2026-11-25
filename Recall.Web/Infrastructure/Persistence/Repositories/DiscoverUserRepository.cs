using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class DiscoverUserRepository(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<DiscoverUserRepository> logger)
    : IDiscoverUserRepository
{
    private const int Series = (int)DiscoverKind.Series;
    private const int Movie = (int)DiscoverKind.Movie;

    public async Task<KnownTitles> GetKnownTitlesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // One statement: a UNION (which also removes the duplicates, such as the
        // hundreds of episode watches of one series) of each table's ids, every
        // part filtered on an index that leads with user_id.
        var known = await dbContext.TrackedSeries.Where(x => x.UserId == userId)
                .Select(x => new KnownRow { Kind = Series, TvdbId = x.TvdbId })
            .Union(dbContext.EpisodeWatches.Where(x => x.UserId == userId)
                .Select(x => new KnownRow { Kind = Series, TvdbId = x.SeriesTvdbId }))
            .Union(dbContext.TrackedMovies.Where(x => x.UserId == userId)
                .Select(x => new KnownRow { Kind = Movie, TvdbId = x.TvdbId }))
            .Union(dbContext.UserMovieWatches.Where(x => x.UserId == userId)
                .Select(x => new KnownRow { Kind = Movie, TvdbId = x.MovieTvdbId }))
            .Union(dbContext.UserLikes.Where(x => x.UserId == userId && x.TargetType != LikeTargetType.Episode)
                .Select(x => new KnownRow { Kind = x.TargetType == LikeTargetType.Movie ? Movie : Series, TvdbId = x.TargetTvdbId }))
            .Union(dbContext.UserRatings.Where(x => x.UserId == userId && x.TargetType != RatingTargetType.Episode)
                .Select(x => new KnownRow { Kind = x.TargetType == RatingTargetType.Movie ? Movie : Series, TvdbId = x.TargetTvdbId }))
            .Union(dbContext.DiscoverDismissals.Where(x => x.UserId == userId)
                .Select(x => new KnownRow { Kind = x.Kind == DiscoverKind.Movie ? Movie : Series, TvdbId = x.TvdbId }))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new KnownTitles(
            known.Where(x => x.Kind == Series).Select(x => x.TvdbId).ToHashSet(),
            known.Where(x => x.Kind == Movie).Select(x => x.TvdbId).ToHashSet());
    }

    public async Task<bool> HideAsync(Guid userId, DiscoverKind kind, int tvdbId, CancellationToken cancellationToken = default)
    {
        if (await dbContext.DiscoverDismissals.AnyAsync(
                x => x.UserId == userId && x.Kind == kind && x.TvdbId == tvdbId, cancellationToken))
            return false;

        var entity = new DiscoverDismissalEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            TvdbId = tvdbId,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        dbContext.DiscoverDismissals.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A second press got there first: hidden either way. Detach the row
            // that lost, or the next save on this context would insert it again.
            dbContext.Entry(entity).State = EntityState.Detached;
            logger.LogInformation("{Kind} {TvdbId} was already hidden from Discover for user {UserId}.", kind, tvdbId, userId);
            return false;
        }
    }

    public async Task<bool> UnhideAsync(Guid userId, DiscoverKind kind, int tvdbId, CancellationToken cancellationToken = default)
    {
        var deleted = await dbContext.DiscoverDismissals
            .Where(x => x.UserId == userId && x.Kind == kind && x.TvdbId == tvdbId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    // A class, not an anonymous type: EF translates the UNION of projections into one type.
    private sealed class KnownRow
    {
        public int Kind { get; init; }
        public int TvdbId { get; init; }
    }
}
