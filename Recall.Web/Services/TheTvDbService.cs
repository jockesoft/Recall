using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Mappings;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Services;

/// <summary>
/// Application-facing TheTVDB service. Owns the read tiering:
/// Redis cache → local Postgres snapshot → TheTVDB API. The API is hit only
/// when neither the cache nor the local DB has the resource.
/// </summary>
public sealed class TheTvDbService(
    ITheTvDbApiClient apiClient,
    IDistributedCacheJson cache,
    ITvdbSnapshotStore store,
    ILogger<TheTvDbService> logger) : ITheTvDbService
{
    private const string Language = "eng";

    public async Task<IReadOnlyList<TvSeriesSummary>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default)
    {
        var items = await apiClient.SearchSeriesAsync(query, cancellationToken);

        return items
            .Where(x => x.Type is null || x.Type.Equals("series", StringComparison.OrdinalIgnoreCase))
            .Select(x => new TvSeriesSummary(
                x.TvdbId,
                x.Name ?? string.Empty,
                x.Overview,
                x.ImageUrl,
                x.Year))
            .ToArray();
    }

    public async Task<TvSeriesDetails?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        var aggregate = await GetSeriesAggregateByIdAsync(seriesId, cancellationToken);
        if (aggregate is null) return null;

        return new TvSeriesDetails(
            aggregate.TvdbId,
            aggregate.Name,
            aggregate.Slug,
            aggregate.Overview,
            aggregate.ImageUrl,
            aggregate.FirstAired?.ToString("yyyy-MM-dd"),
            aggregate.Score,
            aggregate.Status?.Name ?? "");
    }

    private static string AggregateCacheKey(int seriesId, string language) =>
        $"series:aggregate:v1:{seriesId}:{language}";

    public Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
        => GetLayeredAsync<SeriesAggregate>(
            AggregateCacheKey(seriesId, Language),
            ct => store.GetSeriesAggregateAsync(seriesId, Language, ct),
            ct => apiClient.GetSeriesAggregateByIdAsync(seriesId, Language, ct),
            aggregate => store.SaveSeriesAggregateAsync(aggregate, Language, cancellationToken),
            AggregateTtl,
            cancellationToken);

    public async Task<bool> RefreshSeriesAggregateByIdAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        var fresh = await apiClient.GetSeriesAggregateByIdAsync(seriesId, Language, cancellationToken);
        if (fresh is null)
        {
            logger.LogWarning("Refresh skipped for series {SeriesId} — TheTVDB returned no aggregate.", seriesId);
            return false;
        }

        await store.UpsertSeriesAggregateAsync(fresh, Language, cancellationToken);
        await cache.SetAsync(AggregateCacheKey(seriesId, Language), fresh, AggregateTtl(fresh), cancellationToken);

        logger.LogInformation("Refreshed series aggregate {SeriesId} ({EpisodeCount} episodes).", seriesId, fresh.Episodes.Count);
        return true;
    }

    public Task<Series?> GetSeriesByIdExtendedAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
        => GetLayeredAsync<Series>(
            $"series:extended:v2:{seriesId}",
            ct => store.GetSeriesExtendedAsync(seriesId, ct),
            async ct => (await apiClient.GetSeriesByIdExtendedAsync(seriesId, ct))?.ToDomain(),
            series => store.SaveSeriesExtendedAsync(series, cancellationToken),
            _ => Jitter(TimeSpan.FromHours(12), 0.10),
            cancellationToken);

    public Task<Episode?> GetEpisodeDetailsAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
        => GetLayeredAsync<Episode>(
            $"episode:extended:v2:{episodeId}:{Language}",
            ct => store.GetEpisodeExtendedAsync(episodeId, ct),
            async ct => (await apiClient.GetEpisodeInformationByIdAsync(episodeId, ct))?.ToDomain(),
            episode => store.SaveEpisodeExtendedAsync(episode, cancellationToken),
            _ => Jitter(TimeSpan.FromHours(12), 0.10),
            cancellationToken);

    /// <summary>
    /// Read-through the tiers in order: Redis → local DB → API. A value found in
    /// the DB is promoted back into Redis; a value fetched from the API is written
    /// to both. Nulls are never cached, so a transient miss isn't pinned.
    /// </summary>
    private async Task<T?> GetLayeredAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T?>> dbGet,
        Func<CancellationToken, Task<T?>> apiFetch,
        Func<T, Task> dbSave,
        Func<T, TimeSpan> ttl,
        CancellationToken cancellationToken) where T : class
    {
        var cached = await cache.GetAsync<T>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Cache hit for {CacheKey} ({Type}).", cacheKey, typeof(T).Name);
            return cached;
        }

        var stored = await dbGet(cancellationToken);
        if (stored is not null)
        {
            logger.LogDebug("Local DB hit for {CacheKey} ({Type}).", cacheKey, typeof(T).Name);
            await cache.SetAsync(cacheKey, stored, ttl(stored), cancellationToken);
            return stored;
        }

        var fresh = await apiFetch(cancellationToken);
        if (fresh is null)
            return null;

        await dbSave(fresh);
        await cache.SetAsync(cacheKey, fresh, ttl(fresh), cancellationToken);
        return fresh;
    }

    private static TimeSpan AggregateTtl(SeriesAggregate aggregate) =>
        aggregate.Status is { KeepUpdated: false, Name: not null }
        && aggregate.Status.Name.Equals("ended", StringComparison.OrdinalIgnoreCase)
            ? Jitter(TimeSpan.FromDays(7), 0.10)
            : Jitter(TimeSpan.FromHours(12), 0.10);

    private static TimeSpan Jitter(TimeSpan baseTtl, double pct)
    {
        var factor = 1 + (Random.Shared.NextDouble() * 2 - 1) * pct; // e.g. 0.9..1.1
        var ms = Math.Max(1000, baseTtl.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(ms);
    }
}
