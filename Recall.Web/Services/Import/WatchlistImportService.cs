using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Import;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;

namespace Recall.Web.Services.Import;

public sealed class WatchlistImportService(
    IWatchlistImportRepository importRepository,
    ITheTvDbService theTvDbService,
    ITrackedSeriesRepository trackedSeriesRepository,
    IMovieWatchRepository movieWatchRepository,
    ILikeRepository likeRepository,
    IRatingRepository ratingRepository,
    ILogger<WatchlistImportService> logger) : IWatchlistImportService
{
    private static readonly HashSet<string> SupportedSeriesTitleTypes =
        new(StringComparer.OrdinalIgnoreCase) { "TV Series", "TV Mini Series" };

    private const string MovieTitleType = "Movie";

    public async Task<WatchlistImportJob> StartImportAsync(
        Guid userId,
        Stream csvStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (await importRepository.GetActiveJobForUserAsync(userId, cancellationToken) is not null)
            throw new InvalidOperationException("You already have an import in progress.");

        using var reader = new StreamReader(csvStream);
        var parsed = ImdbWatchlistCsvParser.Parse(reader);

        if (parsed.Rows.Count == 0)
            throw new InvalidOperationException("No importable rows were found in that file.");

        var items = parsed.Rows
            .Select(row => new NewWatchlistImportItem(
                row.RowNumber,
                row.ImdbId,
                row.Title,
                row.TitleType,
                row.YourRating,
                IsSupported: IsSupportedTitleType(row.TitleType)))
            .ToArray();

        return await importRepository.CreateJobAsync(userId, fileName, items, cancellationToken);
    }

    public async Task ProcessNextBatchAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var batch = await importRepository.ClaimNextPendingBatchAsync(batchSize, cancellationToken);
        if (batch.Count == 0)
            return;

        var touchedJobIds = new HashSet<Guid>();

        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            touchedJobIds.Add(item.JobId);

            try
            {
                await ProcessItemAsync(item, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "WatchlistImport: failed processing item {ItemId} ({ImdbId}).", item.Id, item.ImdbId);

                await importRepository.MarkItemResultAsync(
                    item.Id,
                    WatchlistImportItemStatus.Failed,
                    resolvedTvdbId: null,
                    "Something went wrong resolving this title.",
                    cancellationToken);
            }
        }

        foreach (var jobId in touchedJobIds)
            await importRepository.RecalculateJobProgressAsync(jobId, cancellationToken);
    }

    private async Task ProcessItemAsync(WatchlistImportItem item, CancellationToken cancellationToken)
    {
        var match = await theTvDbService.ResolveByRemoteIdAsync(item.ImdbId, cancellationToken);
        if (match is null)
        {
            await importRepository.MarkItemResultAsync(
                item.Id, WatchlistImportItemStatus.NotFound, null, "No TheTVDB match for this IMDb id.", cancellationToken);
            return;
        }

        if (match.IsMovie)
            await ProcessMovieAsync(item, match, cancellationToken);
        else
            await ProcessSeriesAsync(item, match, cancellationToken);
    }

    /// <summary>
    /// A rated movie was, by definition, watched — mark it watched and apply the
    /// rating. An unrated row is a genuine "want to watch": there's no separate
    /// movie watchlist in the data model, so it's recorded as a like instead,
    /// same as hearting the movie from its details page.
    /// </summary>
    private async Task ProcessMovieAsync(WatchlistImportItem item, RemoteIdMatch match, CancellationToken cancellationToken)
    {
        if (item.YourRating is { } rating)
        {
            await ratingRepository.RateAsync(
                item.UserId, RatingTargetType.Movie, match.TvdbId, match.TvdbId, rating, cancellationToken);

            var alreadyWatched =
                await movieWatchRepository.GetWatchedUtcAsync(item.UserId, match.TvdbId, cancellationToken) is not null;

            if (!alreadyWatched)
                await movieWatchRepository.ToggleAsync(item.UserId, match.TvdbId, cancellationToken);

            await importRepository.MarkItemResultAsync(
                item.Id,
                WatchlistImportItemStatus.Imported,
                match.TvdbId,
                alreadyWatched
                    ? $"Already watched — rating updated to {rating}/10."
                    : $"Marked watched and rated {rating}/10.",
                cancellationToken);
            return;
        }

        var alreadyLiked = await likeRepository.IsLikedAsync(item.UserId, LikeTargetType.Movie, match.TvdbId, cancellationToken);
        if (!alreadyLiked)
            await likeRepository.ToggleAsync(item.UserId, LikeTargetType.Movie, match.TvdbId, match.TvdbId, cancellationToken);

        await importRepository.MarkItemResultAsync(
            item.Id,
            alreadyLiked ? WatchlistImportItemStatus.AlreadyInLibrary : WatchlistImportItemStatus.Imported,
            match.TvdbId,
            alreadyLiked ? "Already in your favorites." : "Added to your favorites.",
            cancellationToken);
    }

    /// <summary>
    /// Series rows only ever add the show to the library — never touch episodes,
    /// which would mean exactly the kind of TVDB burst this whole import queue
    /// exists to avoid. Mirrors <c>Series/Details</c>'s "Add to library" action.
    /// </summary>
    private async Task ProcessSeriesAsync(WatchlistImportItem item, RemoteIdMatch match, CancellationToken cancellationToken)
    {
        var existing = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(item.UserId, match.TvdbId, cancellationToken);

        if (existing is null)
        {
            var details = await theTvDbService.GetSeriesByIdAsync(match.TvdbId, cancellationToken);
            if (details is null)
            {
                await importRepository.MarkItemResultAsync(
                    item.Id, WatchlistImportItemStatus.NotFound, match.TvdbId,
                    "TheTVDB has no details for this series.", cancellationToken);
                return;
            }

            try
            {
                await trackedSeriesRepository.AddAsync(
                    TrackedSeriesMappings.FromTvDbDetails(item.UserId, details), cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Added concurrently (e.g. the user tracked it manually) between our check and now.
                existing = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(item.UserId, match.TvdbId, cancellationToken);
            }
        }

        if (item.YourRating is { } rating)
        {
            await ratingRepository.RateAsync(
                item.UserId, RatingTargetType.Series, match.TvdbId, match.TvdbId, rating, cancellationToken);
        }

        await importRepository.MarkItemResultAsync(
            item.Id,
            existing is null ? WatchlistImportItemStatus.Imported : WatchlistImportItemStatus.AlreadyInLibrary,
            match.TvdbId,
            existing is null ? "Added to your library." : "Already in your library.",
            cancellationToken);
    }

    private static bool IsSupportedTitleType(string titleType) =>
        string.Equals(titleType, MovieTitleType, StringComparison.OrdinalIgnoreCase)
        || SupportedSeriesTitleTypes.Contains(titleType);
}
