using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.TvdbCache;

public sealed class TvdbSnapshotStore(
    AppDbContext dbContext,
    ILogger<TvdbSnapshotStore> logger)
    : ITvdbSnapshotStore
{
    // Same options Redis/DistributedCacheJson uses — SeriesAggregate already
    // round-trips through this, so the JSON shapes stay in lockstep.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SeriesAggregate?> GetSeriesAggregateAsync(
        int tvdbId, string language, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId && x.Language == language, cancellationToken);

        return Deserialize<SeriesAggregate>(row?.Payload, tvdbId);
    }

    public async Task SaveSeriesAggregateAsync(
        SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .AnyAsync(x => x.TvdbId == aggregate.TvdbId && x.Language == language, cancellationToken);
        if (exists)
            return;

        var entity = new CachedSeriesAggregateEntity
        {
            TvdbId = aggregate.TvdbId,
            Language = language,
            Name = aggregate.Name,
            StatusName = aggregate.Status?.Name,
            KeepUpdated = aggregate.Status?.KeepUpdated,
            Payload = JsonSerializer.Serialize(aggregate, JsonOptions),
            RetrievedUtc = DateTime.UtcNow
        };

        await InsertAsync(entity, aggregate.TvdbId, cancellationToken);
    }

    public async Task<Series?> GetSeriesExtendedAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.CachedSeriesExtended
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId, cancellationToken);

        return Deserialize<Series>(row?.Payload, tvdbId);
    }

    public async Task SaveSeriesExtendedAsync(Series series, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.CachedSeriesExtended
            .AsNoTracking()
            .AnyAsync(x => x.TvdbId == series.Id, cancellationToken);
        if (exists)
            return;

        var entity = new CachedSeriesExtendedEntity
        {
            TvdbId = series.Id,
            Name = series.Name,
            Payload = JsonSerializer.Serialize(series, JsonOptions),
            RetrievedUtc = DateTime.UtcNow
        };

        await InsertAsync(entity, series.Id, cancellationToken);
    }

    public async Task<Episode?> GetEpisodeExtendedAsync(int episodeTvdbId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpisodeTvdbId == episodeTvdbId, cancellationToken);

        return Deserialize<Episode>(row?.Payload, episodeTvdbId);
    }

    public async Task SaveEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (episode.Id is not { } episodeTvdbId)
        {
            logger.LogWarning("Skipping episode snapshot save — episode has no id.");
            return;
        }

        var exists = await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .AnyAsync(x => x.EpisodeTvdbId == episodeTvdbId, cancellationToken);
        if (exists)
            return;

        var entity = new CachedEpisodeExtendedEntity
        {
            EpisodeTvdbId = episodeTvdbId,
            SeriesTvdbId = episode.SeriesId,
            Name = episode.Name,
            Payload = JsonSerializer.Serialize(episode, JsonOptions),
            RetrievedUtc = DateTime.UtcNow
        };

        await InsertAsync(entity, episodeTvdbId, cancellationToken);
    }

    private async Task InsertAsync(object entity, int id, CancellationToken cancellationToken)
    {
        dbContext.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent request already wrote this snapshot — fine, it's insert-only.
            dbContext.Entry(entity).State = EntityState.Detached;
            logger.LogInformation("Snapshot {EntityType} for {Id} already stored by a concurrent request.", entity.GetType().Name, id);
        }
    }

    private T? Deserialize<T>(string? payload, int id) where T : class
    {
        if (string.IsNullOrEmpty(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Treat a corrupt/outdated row as a miss so the caller falls through to the API.
            logger.LogWarning(ex, "Corrupt {Type} snapshot for {Id}; ignoring.", typeof(T).Name, id);
            return null;
        }
    }
}
