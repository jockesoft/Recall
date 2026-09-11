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

    public async Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await GetLayeredAsync<SeriesAggregate>(
            AggregateCacheKey(seriesId, Language),
            ct => store.GetSeriesAggregateAsync(seriesId, Language, ct),
            async ct =>
            {
                var fresh = await apiClient.GetSeriesAggregateByIdAsync(seriesId, Language, ct);
                return fresh is null ? null : await EnrichEpisodesAsync(fresh, ct);
            },
            aggregate => store.SaveSeriesAggregateAsync(aggregate, Language, cancellationToken),
            AggregateTtl,
            cancellationToken);

        // Defensive: heals image paths from any tier (a Postgres snapshot cached
        // before ArtworkUrl.Normalize was introduced would otherwise keep serving
        // broken relative paths forever — see DomainImageNormalization).
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

        fresh = (await EnrichEpisodesAsync(fresh, cancellationToken)).WithNormalizedImages();

        await store.UpsertSeriesAggregateAsync(fresh, Language, cancellationToken);
        await cache.SetAsync(AggregateCacheKey(seriesId, Language), fresh, AggregateTtl(fresh), cancellationToken);

        logger.LogInformation("Refreshed series aggregate {SeriesId} ({EpisodeCount} episodes).", seriesId, fresh.Episodes.Count);

        await BackfillEpisodeImagesAsync(fresh, cancellationToken);

        return true;
    }

    /// <summary>
    /// Fills in each episode's English name/overview. The <c>/series/{id}/extended</c>
    /// fetch behind the aggregate returns episode names/overviews in the show's
    /// original language, so this used to mean one <c>episodes/{id}/translations/eng</c>
    /// call per episode, every time the aggregate was built — for a 100-episode
    /// show, 100 extra requests. Most of those episodes are already sitting in
    /// <c>cached_episode_extended</c> (itself independently kept fresh), already
    /// translated the same way, so reuse that first and only fall back to a live
    /// translation call for episodes we don't have cached yet.
    /// </summary>
    private async Task<SeriesAggregate> EnrichEpisodesAsync(SeriesAggregate aggregate, CancellationToken cancellationToken)
    {
        if (aggregate.Episodes.Count == 0)
            return aggregate;

        var cachedById = await store.GetEpisodesExtendedAsync(
            aggregate.Episodes.Select(e => e.Id).ToArray(), cancellationToken);

        var enriched = await Task.WhenAll(
            aggregate.Episodes.Select(ep => EnrichEpisodeAsync(ep, cachedById, cancellationToken)));

        return aggregate with { Episodes = enriched };
    }

    private async Task<EpisodeSummary> EnrichEpisodeAsync(
        EpisodeSummary episode,
        IReadOnlyDictionary<int, Episode> cachedById,
        CancellationToken cancellationToken)
    {
        if (cachedById.TryGetValue(episode.Id, out var cached))
            return WithTranslation(episode, cached.Name, cached.Overview);

        var translation = await apiClient.GetEpisodeTranslationByLanguageAsync(episode.Id, Language, cancellationToken)
            .AsOptionalAsync(
                logger, LogLevel.Debug,
                "Could not load English translation for episode {EpisodeId}.",
                episode.Id);

        return WithTranslation(episode, translation?.Name, translation?.Overview);
    }

    private static EpisodeSummary WithTranslation(EpisodeSummary episode, string? name, string? overview) =>
        new()
        {
            Id = episode.Id,
            SeasonNumber = episode.SeasonNumber,
            EpisodeNumber = episode.EpisodeNumber,
            Name = !string.IsNullOrWhiteSpace(name) ? name! : episode.Name,
            Overview = !string.IsNullOrWhiteSpace(overview) ? overview : episode.Overview,
            Image = episode.Image,
            Aired = episode.Aired,
            RuntimeMinutes = episode.RuntimeMinutes,
            IsMovie = episode.IsMovie,
            FinaleType = episode.FinaleType
        };

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
            await cache.SetAsync(EpisodeCacheKey(episode.Id!.Value, Language), episode, EpisodeTtl(), cancellationToken);
        }

        logger.LogInformation(
            "Backfilled {Count} episode image(s) for series {SeriesId} from its aggregate refresh.",
            patched.Count, aggregate.TvdbId);
    }

    public Task<MovieAggregate?> GetMovieAggregateByIdAsync(
        int movieId,
        CancellationToken cancellationToken = default)
        => apiClient.GetMovieAggregateByIdAsync(movieId, Language, cancellationToken);

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

    private static string EpisodeCacheKey(int episodeId, string language) =>
        $"episode:extended:v2:{episodeId}:{language}";

    private static TimeSpan EpisodeTtl() => Jitter(TimeSpan.FromHours(12), 0.10);

    public async Task<Episode?> GetEpisodeDetailsAsync(
        int episodeId,
        CancellationToken cancellationToken = default)
    {
        var episode = await GetLayeredAsync<Episode>(
            EpisodeCacheKey(episodeId, Language),
            ct => store.GetEpisodeExtendedAsync(episodeId, ct),
            async ct => (await apiClient.GetEpisodeInformationByIdAsync(episodeId, ct))?.ToDomain(),
            episode => store.SaveEpisodeExtendedAsync(episode, cancellationToken),
            _ => EpisodeTtl(),
            cancellationToken);

        // Defensive: see DomainImageNormalization / GetSeriesAggregateByIdAsync.
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
        await cache.SetAsync(EpisodeCacheKey(episodeId, Language), fresh, EpisodeTtl(), cancellationToken);

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
