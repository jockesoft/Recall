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
    public async Task<IReadOnlyList<FavoriteTitle>> GetLikedTitlesAsync(
        Guid userId,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        // Sequential: both share the request's scoped DbContext (like the series
        // aggregate calls below, that one uses a pooled DbContext factory instead,
        // so it's safe to run those in parallel).
        var seriesLikes = await likeRepository.GetLikesAsync(userId, LikeTargetType.Series, cancellationToken);
        var movieLikes = await likeRepository.GetLikesAsync(userId, LikeTargetType.Movie, cancellationToken);

        var ordered = seriesLikes
            .Select(l => (l.TargetTvdbId, l.CreatedUtc, Type: SearchResultType.Series))
            .Concat(movieLikes.Select(l => (l.TargetTvdbId, l.CreatedUtc, Type: SearchResultType.Movie)))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();

        if (limit is { } max)
            ordered = ordered.Take(Math.Max(0, max)).ToList();

        if (ordered.Count == 0)
            return [];

        var seriesIds = ordered.Where(x => x.Type == SearchResultType.Series).Select(x => x.TargetTvdbId).Distinct().ToList();
        var movieIds = ordered.Where(x => x.Type == SearchResultType.Movie).Select(x => x.TargetTvdbId).Distinct().ToList();

        // One batched query on the scoped DbContext for every watched episode
        // across these series — must finish before the aggregate fan-out below,
        // which uses a pooled DbContext factory and is safe to run concurrently
        // with itself (but not with another call on the shared context).
        var watchedIds = seriesIds.Count > 0
            ? await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken)
            : new HashSet<int>();

        var seriesAggregatesTask = seriesIds.Count > 0
            ? Task.WhenAll(seriesIds.Select(id => theTvDbService.TryGetSeriesAggregateAsync(id, logger, nameof(FavoritesService), cancellationToken)))
            : Task.FromResult(Array.Empty<SeriesAggregate?>());

        var movieAggregatesTask = movieIds.Count > 0
            ? Task.WhenAll(movieIds.Select(id => theTvDbService.TryGetMovieAggregateAsync(id, logger, nameof(FavoritesService), cancellationToken)))
            : Task.FromResult(Array.Empty<MovieAggregate?>());

        await Task.WhenAll(seriesAggregatesTask, movieAggregatesTask);

        var seriesById = seriesAggregatesTask.Result.Where(a => a is not null).Select(a => a!).ToDictionary(a => a.TvdbId);
        var moviesById = movieAggregatesTask.Result.Where(a => a is not null).Select(a => a!).ToDictionary(a => a.TvdbId);

        var result = new List<FavoriteTitle>(ordered.Count);
        foreach (var entry in ordered)
        {
            if (entry.Type == SearchResultType.Series)
            {
                if (!seriesById.TryGetValue(entry.TargetTvdbId, out var aggregate))
                    continue;

                var progress = watchProgressService.BuildProgress(
                    aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedIds);

                result.Add(new FavoriteTitle(
                    SearchResultType.Series,
                    aggregate.TvdbId,
                    aggregate.Name,
                    aggregate.ImageUrl,
                    aggregate.FirstAired,
                    progress.WatchedReleasedCount,
                    progress.ReleasedCount));
            }
            else
            {
                if (!moviesById.TryGetValue(entry.TargetTvdbId, out var movie))
                    continue;

                result.Add(new FavoriteTitle(
                    SearchResultType.Movie,
                    movie.TvdbId,
                    movie.Name,
                    movie.ImageUrl,
                    movie.ReleaseDate,
                    WatchedEpisodes: 0,
                    ReleasedEpisodes: 0));
            }
        }

        // `ordered` keeps the newest-liked-first order across both types.
        return result;
    }

    public async Task<FavoritesView> GetAllFavoritesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Sequential: both halves read from the scoped DbContext (likes +
        // watched episodes), so they must not overlap.
        var titles = await GetLikedTitlesAsync(userId, limit: null, cancellationToken);
        var episodes = await GetLikedEpisodesAsync(userId, cancellationToken);

        return new FavoritesView(titles, titles.Count, episodes, episodes.Count);
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
            seriesIds.Select(id => theTvDbService.TryGetSeriesAggregateAsync(id, logger, nameof(FavoritesService), cancellationToken)));

        var bySeriesId = aggregates
            .Where(a => a is not null)
            .Select(a => a!)
            .ToDictionary(a => a.TvdbId);

        var built = await Task.WhenAll(
            likes.Select(like => BuildFavoriteEpisodeAsync(like, bySeriesId, cancellationToken)));

        return built.Where(e => e is not null).Select(e => e!).ToList();
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
                imageUrl,
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
