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
}
