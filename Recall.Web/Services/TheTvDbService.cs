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

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var items = await apiClient.SearchAsync(query, cancellationToken);

        return items
            .Select(x => x.ToDomain())
            .OfType<SearchResultItem>()
            .ToArray();
    }

    public async Task<RemoteIdMatch?> ResolveByRemoteIdAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        var results = await apiClient.SearchByRemoteIdAsync(remoteId, cancellationToken);

        foreach (var result in results)
        {
            if (result.Movie is { } movie)
                return new RemoteIdMatch(movie.Id, movie.Name, IsMovie: true);

            if (result.Series is { } series)
                return new RemoteIdMatch(series.Id, series.Name, IsMovie: false);
        }

        return null;
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

    public async Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await GetLayeredAsync<SeriesAggregate>(
            TvdbCacheKeys.SeriesAggregate(seriesId, Language),
            ct => store.GetSeriesAggregateAsync(seriesId, Language, ct),
            // Episode names and overviews are already in English: the client
            // reads them from TheTVDB's translated episode list, a fixed few
            // requests per series however many episodes it has.
            ct => apiClient.GetSeriesAggregateByIdAsync(seriesId, Language, ct),
            aggregate => store.SaveSeriesAggregateAsync(aggregate, Language, cancellationToken),
            cancellationToken);

        // Defensive: heals image paths from any tier (a Postgres snapshot cached
        // before ArtworkUrl.Normalize was introduced would otherwise keep serving
        // broken relative paths forever — see DomainImageNormalization).
        return aggregate?.WithNormalizedImages();
    }

    public async Task<SeriesAggregate?> GetCachedSeriesAggregateAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        // The same layers as GetSeriesAggregateByIdAsync without the last one:
        // Redis, then the Postgres snapshot (written back up to Redis), and
        // nothing when neither has it. "Nothing" is not cached.
        var aggregate = await GetLayeredAsync<SeriesAggregate>(
            TvdbCacheKeys.SeriesAggregate(seriesId, Language),
            ct => store.GetSeriesAggregateAsync(seriesId, Language, ct),
            _ => Task.FromResult<SeriesAggregate?>(null),
            _ => Task.CompletedTask,
            cancellationToken);

        return aggregate?.WithNormalizedImages();
    }

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

        fresh = fresh.WithNormalizedImages();

        await store.UpsertSeriesAggregateAsync(fresh, Language, cancellationToken);
        await cache.SetAsync(TvdbCacheKeys.SeriesAggregate(seriesId, Language), fresh, RedisTtl(), cancellationToken);

        logger.LogInformation("Refreshed series aggregate {SeriesId} ({EpisodeCount} episodes).", seriesId, fresh.Episodes.Count);

        await BackfillEpisodeImagesAsync(fresh, cancellationToken);

        return true;
    }

    /// <summary>
    /// The aggregate sometimes has a per-episode still before the dedicated
    /// episode endpoint does. Since we already paid for this fetch, patch any
    /// matching <c>cached_episode_extended</c> row that's still missing an
    /// image — no extra TheTVDB call — and refresh its Redis entry so a live
    /// read doesn't serve the stale null-image copy for the rest of its TTL.
    /// </summary>
    private async Task BackfillEpisodeImagesAsync(SeriesAggregate aggregate, CancellationToken cancellationToken)
    {
        var patched = await store.BackfillEpisodeImagesFromAggregateAsync(aggregate, cancellationToken);
        if (patched.Count == 0)
            return;

        foreach (var episode in patched)
        {
            await cache.SetAsync(TvdbCacheKeys.EpisodeExtended(episode.Id!.Value, Language), episode, RedisTtl(), cancellationToken);
        }

        logger.LogInformation(
            "Backfilled {Count} episode image(s) for series {SeriesId} from its aggregate refresh.",
            patched.Count, aggregate.TvdbId);
    }

    public async Task<MovieAggregate?> GetMovieAggregateByIdAsync(
        int movieId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await GetLayeredAsync<MovieAggregate>(
            TvdbCacheKeys.MovieAggregate(movieId, Language),
            ct => store.GetMovieAggregateAsync(movieId, Language, ct),
            ct => apiClient.GetMovieAggregateByIdAsync(movieId, Language, ct),
            aggregate => store.SaveMovieAggregateAsync(aggregate, Language, cancellationToken),
            cancellationToken);

        // Defensive: see DomainImageNormalization / GetSeriesAggregateByIdAsync.
        return aggregate?.WithNormalizedImages();
    }

    public async Task<MovieAggregate?> GetCachedMovieAggregateAsync(
        int movieId,
        CancellationToken cancellationToken = default)
    {
        // As GetCachedSeriesAggregateAsync: the two cache tiers and no API.
        var aggregate = await GetLayeredAsync<MovieAggregate>(
            TvdbCacheKeys.MovieAggregate(movieId, Language),
            ct => store.GetMovieAggregateAsync(movieId, Language, ct),
            _ => Task.FromResult<MovieAggregate?>(null),
            _ => Task.CompletedTask,
            cancellationToken);

        return aggregate?.WithNormalizedImages();
    }

    public async Task<bool> RefreshMovieAggregateByIdAsync(
        int movieId,
        CancellationToken cancellationToken = default)
    {
        var fresh = (await apiClient.GetMovieAggregateByIdAsync(movieId, Language, cancellationToken))?.WithNormalizedImages();
        if (fresh is null)
        {
            logger.LogWarning("Refresh skipped for movie {MovieId} — TheTVDB returned no aggregate.", movieId);
            return false;
        }

        await store.UpsertMovieAggregateAsync(fresh, Language, cancellationToken);
        await cache.SetAsync(TvdbCacheKeys.MovieAggregate(movieId, Language), fresh, RedisTtl(), cancellationToken);

        logger.LogInformation("Refreshed movie aggregate {MovieId} (\"{Name}\").", movieId, fresh.Name);
        return true;
    }

    public Task<Series?> GetSeriesByIdExtendedAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
        => GetLayeredAsync<Series>(
            TvdbCacheKeys.SeriesExtended(seriesId),
            ct => store.GetSeriesExtendedAsync(seriesId, ct),
            async ct => (await apiClient.GetSeriesByIdExtendedAsync(seriesId, ct))?.ToDomain(),
            series => store.SaveSeriesExtendedAsync(series, cancellationToken),
            cancellationToken);

    public async Task<Episode?> GetEpisodeDetailsAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
    {
        var episode = await GetLayeredAsync<Episode>(
            TvdbCacheKeys.EpisodeExtended(episodeId, Language),
            ct => store.GetEpisodeExtendedAsync(episodeId, ct),
            async ct => (await apiClient.GetEpisodeInformationByIdAsync(episodeId, ct))?.ToDomain(),
            episode => store.SaveEpisodeExtendedAsync(episode, cancellationToken),
            cancellationToken);

        // Defensive: see DomainImageNormalization / GetSeriesAggregateByIdAsync.
        return episode?.WithNormalizedImages();
    }

    public async Task<Episode?> GetCachedEpisodeDetailsAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
    {
        // As GetCachedSeriesAggregateAsync: the two cache tiers and no API.
        var episode = await GetLayeredAsync<Episode>(
            TvdbCacheKeys.EpisodeExtended(episodeId, Language),
            ct => store.GetEpisodeExtendedAsync(episodeId, ct),
            _ => Task.FromResult<Episode?>(null),
            _ => Task.CompletedTask,
            cancellationToken);

        return episode?.WithNormalizedImages();
    }

    public async Task<bool> RefreshEpisodeDetailsByIdAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
    {
        var fresh = (await apiClient.GetEpisodeInformationByIdAsync(episodeId, cancellationToken))?.ToDomain()?.WithNormalizedImages();
        if (fresh is null)
        {
            logger.LogWarning("Refresh skipped for episode {EpisodeId} — TheTVDB returned no episode.", episodeId);
            return false;
        }

        await store.UpsertEpisodeExtendedAsync(fresh, cancellationToken);
        await cache.SetAsync(TvdbCacheKeys.EpisodeExtended(episodeId, Language), fresh, RedisTtl(), cancellationToken);

        logger.LogInformation("Refreshed episode snapshot {EpisodeId} (\"{Name}\").", episodeId, fresh.Name);
        return true;
    }

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
            await cache.SetAsync(cacheKey, stored, RedisTtl(), cancellationToken);
            return stored;
        }

        var fresh = await apiFetch(cancellationToken);
        if (fresh is null)
            return null;

        await dbSave(fresh);
        await cache.SetAsync(cacheKey, fresh, RedisTtl(), cancellationToken);
        return fresh;
    }

    /// <summary>
    /// How long a Redis entry lives: 7 days, ±10% so a burst of writes (Redis
    /// restarted, a backfill) does not all expire in the same hour. Every
    /// resource gets the same, because the lifetime decides nothing about
    /// freshness: a miss is served from the Postgres snapshot, which has no
    /// staleness check, and never from TheTVDB. What keeps the data fresh is the
    /// refresh jobs, and every refresh (and the episode still backfill) rewrites
    /// the Redis entry along with the snapshot, so an entry is never older than
    /// its row; a shorter lifetime only meant more misses and more rewrites.
    /// Until 2026-11-14 it was 12 hours for anything that could still change
    /// (7 days for ended series and released movies), and keys lived about 15
    /// hours on average in production. Not sliding: a sliding entry costs a
    /// write (an EXPIRE) on every read. Memory is bounded by Redis itself
    /// (<c>maxmemory</c> with LRU eviction in <c>compose.prod.yml</c>).
    /// </summary>
    public static readonly TimeSpan RedisLifetime = TimeSpan.FromDays(7);

    private static TimeSpan RedisTtl() => Jitter(RedisLifetime, 0.10);

    private static TimeSpan Jitter(TimeSpan baseTtl, double pct)
    {
        var factor = 1 + (Random.Shared.NextDouble() * 2 - 1) * pct; // e.g. 0.9..1.1
        var ms = Math.Max(1000, baseTtl.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(ms);
    }
}
