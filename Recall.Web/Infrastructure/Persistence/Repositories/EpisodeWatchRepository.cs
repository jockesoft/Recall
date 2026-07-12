using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        dbContext.EpisodeWatches.Add(new()
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
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
}


