using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Favorites.Models;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Favorites;

public sealed class FavoritesService(
    ILikeRepository likeRepository,
    ITheTvDbService theTvDbService,
    IEpisodeWatchRepository episodeWatchRepository,
    IWatchProgressService watchProgressService,
    ILogger<FavoritesService> logger)
    : IFavoritesService
{
    // TheTVDB hands back episode stills in a few shapes: the series aggregate
    // uses site-relative paths ("/banners/episodes/266967/5129617.jpg" or
    // "/banners/v4/episode/.../screencap/...jpg"), while /episodes/{id} returns
    // an absolute URL. Leave absolute URLs alone; prefix everything else onto the
    // artwork host (tolerating a missing leading slash).
    private const string ArtworkBaseUrl = "https://artworks.thetvdb.com";

    private static string? NormalizeArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim();

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{ArtworkBaseUrl}/{url.TrimStart('/')}";
    }

    public async Task<IReadOnlyList<FavoriteSeries>> GetLikedSeriesAsync(
        Guid userId,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var likes = await likeRepository.GetLikesAsync(userId, LikeTargetType.Series, cancellationToken);

        var ids = likes
            .Select(l => l.TargetTvdbId)
            .Distinct()
            .ToList();

        if (limit is { } max)
            ids = ids.Take(Math.Max(0, max)).ToList();

        if (ids.Count == 0)
            return [];

        // One batched query on the scoped DbContext for every watched episode
        // across these series — then the TheTVDB aggregates fan out in parallel
        // (that path uses a pooled DbContext factory, so it's concurrency-safe).
        var watchedIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, ids, cancellationToken);

        var aggregates = await Task.WhenAll(
            ids.Select(id => TryGetAggregateAsync(id, cancellationToken)));

        var result = new List<FavoriteSeries>(ids.Count);
        foreach (var aggregate in aggregates)
        {
            if (aggregate is null)
                continue;

            var progress = watchProgressService.BuildProgress(
                aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedIds);

            result.Add(new FavoriteSeries(
                aggregate.TvdbId,
                aggregate.Name,
                aggregate.ImageUrl,
                aggregate.FirstAired,
                progress.WatchedReleasedCount,
                progress.ReleasedCount));
        }

        // Aggregates keep the newest-liked-first order of `ids`.
        return result;
    }

    public async Task<FavoritesView> GetAllFavoritesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Sequential: both halves read from the scoped DbContext (likes +
        // watched episodes), so they must not overlap.
        var series = await GetLikedSeriesAsync(userId, limit: null, cancellationToken);
        var episodes = await GetLikedEpisodesAsync(userId, cancellationToken);

        return new FavoritesView(series, series.Count, episodes, episodes.Count);
    }

    private async Task<IReadOnlyList<FavoriteEpisode>> GetLikedEpisodesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var likes = await likeRepository.GetLikesAsync(userId, LikeTargetType.Episode, cancellationToken);
        if (likes.Count == 0)
            return [];

        // One aggregate per distinct parent series — covers the series name plus,
        // for most episodes, the still image and numbering without a per-episode
        // round trip.
        var seriesIds = likes
            .Select(l => l.SeriesTvdbId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var aggregates = await Task.WhenAll(
            seriesIds.Select(id => TryGetAggregateAsync(id, cancellationToken)));

        var bySeriesId = aggregates
            .Where(a => a is not null)
            .Select(a => a!)
            .ToDictionary(a => a.TvdbId);

        var built = await Task.WhenAll(
            likes.Select(like => BuildFavoriteEpisodeAsync(like, bySeriesId, cancellationToken)));

        return built.Where(e => e is not null).Select(e => e!).ToList();
    }

    private async Task<SeriesAggregate?> TryGetAggregateAsync(int seriesTvdbId, CancellationToken cancellationToken)
    {
        try
        {
            return await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load series {SeriesId} for favorites.", seriesTvdbId);
            return null;
        }
    }

    private async Task<FavoriteEpisode?> BuildFavoriteEpisodeAsync(
        UserLike like,
        IReadOnlyDictionary<int, SeriesAggregate> bySeriesId,
        CancellationToken cancellationToken)
    {
        try
        {
            bySeriesId.TryGetValue(like.SeriesTvdbId, out var aggregate);
            var summary = aggregate?.Episodes.FirstOrDefault(e => e.Id == like.TargetTvdbId);

            var seriesName = aggregate?.Name;
            var episodeName = summary?.Name;
            var seasonNumber = summary?.SeasonNumber;
            var episodeNumber = summary?.EpisodeNumber;
            var imageUrl = summary?.Image;

            if (summary is null || string.IsNullOrWhiteSpace(imageUrl))
            {
                // Fall back to a direct episode lookup when the liked episode
                // isn't in the cached aggregate (a special, a removed entry, or a
                // stale snapshot) OR when the aggregate has the episode but no
                // still — the /episodes/{id} endpoint (same source Episodes/Details
                // uses) usually has the image the aggregate is missing. This path
                // also uses the pooled DbContext factory, so running it in
                // parallel is safe.
                var episode = await theTvDbService.GetEpisodeDetailsAsync(like.TargetTvdbId, cancellationToken);
                if (episode is null && aggregate is null)
                    return null;

                episodeName ??= episode?.Name;
                seasonNumber ??= episode?.SeasonNumber;
                episodeNumber ??= episode?.Number;

                if (string.IsNullOrWhiteSpace(imageUrl))
                    imageUrl = episode?.Image;
            }

            return new FavoriteEpisode(
                like.TargetTvdbId,
                string.IsNullOrWhiteSpace(seriesName) ? "Unknown series" : seriesName,
                NormalizeArtworkUrl(imageUrl),
                seasonNumber,
                episodeNumber,
                episodeName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load liked episode {EpisodeId} for favorites.", like.TargetTvdbId);
            return null;
        }
    }
}
