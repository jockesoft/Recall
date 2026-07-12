using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class EpisodeWatchRepository(
    AppDbContext dbContext,
    ILogger<EpisodeWatchRepository> logger)
    : IEpisodeWatchRepository
{
    public Task<bool> IsWatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default)
    {
        return dbContext.EpisodeWatches
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.EpisodeTvdbId == episodeTvdbId, cancellationToken);
    }

    public async Task<IReadOnlySet<int>> GetWatchedEpisodeIdsAsync(
        Guid userId,
        IEnumerable<int> seriesTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var seriesIds = seriesTvdbIds as ICollection<int> ?? seriesTvdbIds.ToList();
        if (seriesIds.Count == 0)
            return new HashSet<int>();

        var ids = await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId && seriesIds.Contains(x.SeriesTvdbId))
            .Select(x => x.EpisodeTvdbId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task MarkWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        dbContext.EpisodeWatches.Add(new EpisodeWatchEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SeriesTvdbId = seriesTvdbId,
            EpisodeTvdbId = episodeTvdbId,
            WatchedUtc = DateTime.UtcNow
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation(
                ex,
                "Episode watch already exists for user {UserId}, episode {EpisodeTvdbId}.",
                userId,
                episodeTvdbId);
        }
    }

    public async Task MarkUnwatchedAsync(Guid userId, int episodeTvdbId, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.EpisodeWatches
            .FirstOrDefaultAsync(x => x.UserId == userId && x.EpisodeTvdbId == episodeTvdbId, cancellationToken);

        if (existing is null)
            return;

        dbContext.EpisodeWatches.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
    
    public async Task<IReadOnlySet<int>> GetWatchedEpisodeIdsAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var ids = await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.SeriesTvdbId == seriesTvdbId)
            .Select(x => x.EpisodeTvdbId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task MarkWatchedRangeAsync(
        Guid userId,
        int seriesTvdbId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var ids = episodeTvdbIds as ICollection<int> ?? episodeTvdbIds.ToList();
        if (ids.Count == 0)
            return;

        var alreadyWatched = await dbContext.EpisodeWatches
            .Where(x => x.UserId == userId && ids.Contains(x.EpisodeTvdbId))
            .Select(x => x.EpisodeTvdbId)
            .ToListAsync(cancellationToken);

        var alreadyWatchedSet = alreadyWatched.ToHashSet();

        var toInsert = ids
            .Where(id => !alreadyWatchedSet.Contains(id))
            .Select(id => new EpisodeWatchEntity
            {
                UserId = userId,
                SeriesTvdbId = seriesTvdbId,
                EpisodeTvdbId = id,
                WatchedUtc = DateTime.UtcNow
            })
            .ToList();

        if (toInsert.Count == 0)
            return;

        await dbContext.EpisodeWatches.AddRangeAsync(toInsert, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}


