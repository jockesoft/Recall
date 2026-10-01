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

    public async Task<IReadOnlyDictionary<int, DateTime>> GetWatchedUtcByEpisodeAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.SeriesTvdbId == seriesTvdbId)
            .Select(x => new { x.EpisodeTvdbId, x.WatchedUtc })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.EpisodeTvdbId)
            .ToDictionary(g => g.Key, g => g.Min(r => r.WatchedUtc));
    }

    public async Task<DateTime?> GetWatchedUtcAsync(
        Guid userId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.EpisodeTvdbId == episodeTvdbId)
            .OrderBy(x => x.WatchedUtc)
            .Select(x => (DateTime?)x.WatchedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, DateTime>> GetLastWatchedUtcBySeriesAsync(
        Guid userId,
        IEnumerable<int> seriesTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var seriesIds = seriesTvdbIds as ICollection<int> ?? seriesTvdbIds.ToList();
        if (seriesIds.Count == 0)
            return new Dictionary<int, DateTime>();

        var rows = await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId && seriesIds.Contains(x.SeriesTvdbId))
            .GroupBy(x => x.SeriesTvdbId)
            .Select(g => new { SeriesTvdbId = g.Key, LastWatchedUtc = g.Max(x => x.WatchedUtc) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.SeriesTvdbId, r => r.LastWatchedUtc);
    }

    public async Task<IReadOnlyList<int>> GetWatchedSeriesTvdbIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.EpisodeWatches
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.SeriesTvdbId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<WatchedBatch> MarkWatchedRangeAsync(
        Guid userId,
        int seriesTvdbId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var ids = episodeTvdbIds as ICollection<int> ?? episodeTvdbIds.ToList();
        if (ids.Count == 0)
            return WatchedBatch.Empty;

        var alreadyWatched = await dbContext.EpisodeWatches
            .Where(x => x.UserId == userId && ids.Contains(x.EpisodeTvdbId))
            .Select(x => x.EpisodeTvdbId)
            .ToListAsync(cancellationToken);

        var alreadyWatchedSet = alreadyWatched.ToHashSet();

        // One timestamp for the whole batch, truncated to the millisecond: the
        // undo finds these rows again by comparing WatchedUtc for equality, and
        // Postgres keeps microseconds where .NET keeps 100 ns ticks — an
        // untruncated value would not survive the round trip unchanged.
        var now = DateTime.UtcNow;
        var batchWatchedUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        var toInsert = ids
            .Distinct()
            .Where(id => !alreadyWatchedSet.Contains(id))
            .Select(id => new EpisodeWatchEntity
            {
                UserId = userId,
                SeriesTvdbId = seriesTvdbId,
                EpisodeTvdbId = id,
                WatchedUtc = batchWatchedUtc
            })
            .ToList();

        if (toInsert.Count == 0)
            return WatchedBatch.Empty;

        await dbContext.EpisodeWatches.AddRangeAsync(toInsert, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new WatchedBatch(toInsert.Count, batchWatchedUtc);
    }

    public Task<int> UndoWatchedBatchAsync(
        Guid userId,
        int seriesTvdbId,
        DateTime batchWatchedUtc,
        CancellationToken cancellationToken = default)
    {
        return dbContext.EpisodeWatches
            .Where(x => x.UserId == userId
                        && x.SeriesTvdbId == seriesTvdbId
                        && x.WatchedUtc == batchWatchedUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> MarkUnwatchedRangeAsync(
        Guid userId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var ids = episodeTvdbIds as ICollection<int> ?? episodeTvdbIds.ToList();
        if (ids.Count == 0)
            return 0;

        return await dbContext.EpisodeWatches
            .Where(x => x.UserId == userId && ids.Contains(x.EpisodeTvdbId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
