using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Services;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class EpisodeWatchRepository(
    AppDbContext dbContext,
    ITheTvDbService theTvDbService,
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
    
    /// <summary>
    /// Counts unwatched episodes strictly before <paramref name="currentEpisode"/>
    /// in season/episode order. Fails closed (returns 0, no modal shown) rather
    /// than letting a transient error here block the whole page — this is a
    /// nice-to-have prompt, not something worth a broken page over.
    /// </summary>
    public async Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesId,
        Episode currentEpisode,
        CancellationToken cancellationToken)
    {
        try
        {
            var ordered = await GetOrderedEpisodesAsync(seriesId, cancellationToken);

            var currentIndex = ordered.FindIndex(e => e.Id == currentEpisode.Id);
            if (currentIndex <= 0)
                return 0;

            var priorIds = ordered.Take(currentIndex).Select(e => e.Id).ToList();
            if (priorIds.Count == 0)
                return 0;

            var watchedIds = await GetWatchedEpisodeIdsAsync(userId, seriesId, cancellationToken);

            return priorIds.Count(pid => !watchedIds.Contains(pid!.Value));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not compute prior-unwatched count for episode {EpisodeId}.", currentEpisode.Id);
            return 0;
        }
    }
    
    /// <summary>
    /// Single source of truth for "the ordered, non-movie episode list for a series."
    /// Used by both the count shown in the confirmation modal and the actual
    /// mark-through action — kept in one place so those two can never disagree
    /// about which episodes count as "earlier."
    /// </summary>
    public async Task<List<Episode>> GetOrderedEpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var serie = await theTvDbService.GetSeriesByIdExtendedAsync(seriesId, cancellationToken);
        var episodes = serie?.Episodes ?? [];

        return episodes
            .Where(e => e is { Id: not null, IsMovie: false })
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.Number ?? int.MaxValue)
            .ToList();
    }
}


