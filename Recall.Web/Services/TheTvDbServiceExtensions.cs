using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services;

public static class TheTvDbServiceExtensions
{
    /// <summary>
    /// Best-effort series aggregate fetch: swallows any exception except
    /// cancellation, logs a warning tagged with <paramref name="context"/>, and
    /// returns <c>null</c> instead of letting one bad series take down a page or
    /// job that's loading many of them in parallel.
    /// </summary>
    public static async Task<SeriesAggregate?> TryGetSeriesAggregateAsync(
        this ITheTvDbService theTvDbService,
        int seriesTvdbId,
        ILogger logger,
        string context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Context}: could not load series aggregate {SeriesId}.", context, seriesTvdbId);
            return null;
        }
    }

    /// <summary>
    /// Best-effort movie aggregate fetch: swallows any exception except
    /// cancellation, logs a warning tagged with <paramref name="context"/>, and
    /// returns <c>null</c> instead of letting one bad movie take down a page or
    /// job that's loading many of them in parallel.
    /// </summary>
    public static async Task<MovieAggregate?> TryGetMovieAggregateAsync(
        this ITheTvDbService theTvDbService,
        int movieTvdbId,
        ILogger logger,
        string context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await theTvDbService.GetMovieAggregateByIdAsync(movieTvdbId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Context}: could not load movie aggregate {MovieId}.", context, movieTvdbId);
            return null;
        }
    }

    /// <summary>
    /// The art for an episode by <see cref="EpisodeArt.Resolve"/>'s rule. When
    /// the aggregate has no still for it and the caller has not already loaded
    /// the episode's own record, that record is read (layered cache; fetched
    /// from TheTVDB the first time, which also puts the episode in the refresh
    /// job's care, so a still that arrives later is picked up). Best-effort:
    /// a failed lookup falls through to the background art.
    /// </summary>
    public static async Task<EpisodeArt> GetEpisodeArtAsync(
        this ITheTvDbService theTvDbService,
        SeriesAggregate? series,
        int episodeId,
        ILogger logger,
        Episode? record = null,
        CancellationToken cancellationToken = default)
    {
        if (record is null && EpisodeArt.StillInAggregate(series, episodeId) is null)
        {
            try
            {
                record = await theTvDbService.GetEpisodeDetailsAsync(episodeId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not load episode {EpisodeId} to look for its still.", episodeId);
            }
        }

        return EpisodeArt.Resolve(series, episodeId, record);
    }
}
