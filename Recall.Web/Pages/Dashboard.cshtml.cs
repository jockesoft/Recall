using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages;

// ---------------------------------------------------------------------------
// View DTOs — match the properties used in Index.cshtml
// ---------------------------------------------------------------------------

public sealed class UpcomingEpisodeItem
{
    public int SeriesId { get; init; }
    public string SeriesName { get; init; } = "";
    public int EpisodeId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = "";
    public string? ImageUrl { get; init; }
    public DateOnly AiredDate { get; init; }

    /// <summary>TheTVDB finale marker ("season", "series", "midseason"), if any.</summary>
    public string? FinaleType { get; init; }

    /// <summary>True when the user has watched every aired episode of this series.</summary>
    public bool SeriesCaughtUp { get; init; }
}

public sealed record CatchUpItem
{
    public int SeriesId { get; init; }
    public string SeriesName { get; init; } = "";
    public int EpisodeId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = "";

    /// <summary>
    /// Still for the next episode; without one, the series' background art
    /// (fanart), which is also 16:9. Null when the series has neither — the
    /// card then shows a dark placeholder. Never the poster: a portrait cover
    /// cropped to 16:9 shows a strip of it.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// True when the series aggregate already had this episode's own still, so
    /// there is nothing left to look up. Not rendered.
    /// </summary>
    public bool HasEpisodeImage { get; init; }
}

// ---------------------------------------------------------------------------
// Page model
// ---------------------------------------------------------------------------

[Authorize]
public sealed class DashboardModel(
    ITheTvDbService theTvDbService,
    ITrackedSeriesRepository libraryRepository,
    IEpisodeWatchRepository watchedRepository,
    IWatchProgressService watchProgressService,
    ILogger<DashboardModel> logger,
    ICurrentUserService currentUserService,
    TimeProvider timeProvider,
    IOptions<LibraryOptions> libraryOptions) : PageModel
{
    /// <summary>Today's date in UTC — the date every air-date comparison on this page (and its view) uses.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    private const int UpcomingWindowDays = 30;
    private const int ThisWeekWindowDays = 7;

    public int TrackedSeriesCount { get; private set; }
    public int UpcomingThisWeekCount { get; private set; }
    public int UnwatchedCount { get; private set; }
    public List<UpcomingEpisodeItem> UpcomingEpisodes { get; private set; } = [];
    public List<CatchUpItem> CatchUpEpisodes { get; private set; } = [];

    /// <summary>
    /// Series in the queue that the user hasn't watched in a while. They get no
    /// card here: the page links to them in the Library instead.
    /// </summary>
    public int DormantSeriesCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
        var trackedSeriesIds = await libraryRepository.GetByUserAsync(userId, cancellationToken);
        TrackedSeriesCount = trackedSeriesIds.Count;

        if (trackedSeriesIds.Count == 0)
            return;

        var aggregates = (await Task.WhenAll(
                trackedSeriesIds.Select(id => theTvDbService.TryGetSeriesAggregateAsync(id.TvdbId, logger, nameof(DashboardModel), cancellationToken))))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        var seriesIds = aggregates.Select(a => a.TvdbId).ToList();
        var watchedIds = await watchedRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken);

        var today = Today;
        var upcomingCutoff = today.AddDays(UpcomingWindowDays);
        var thisWeekCutoff = today.AddDays(ThisWeekWindowDays);

        var upcoming = new List<UpcomingEpisodeItem>();
        var catchUp = new List<CatchUpItem>();
        var recentPremieres = new HashSet<int>();
        var unwatchedTotal = 0;

        foreach (var aggregate in aggregates)
        {
            // Next episode to watch + unwatched count: shared logic, same rule as
            // the series page (earliest aired episode not marked watched).
            var progress = watchProgressService.BuildProgress(aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedIds);
            unwatchedTotal += progress.UnwatchedReleasedCount;

            var seriesCaughtUp = progress.UnwatchedReleasedCount == 0;

            foreach (var ep in aggregate.Episodes.Where(e => e.Aired is { } aired && aired >= today && aired <= upcomingCutoff))
            {
                upcoming.Add(new UpcomingEpisodeItem
                {
                    SeriesId = aggregate.TvdbId,
                    SeriesName = aggregate.Name,
                    EpisodeId = ep.Id,
                    SeasonNumber = ep.SeasonNumber,
                    EpisodeNumber = ep.EpisodeNumber,
                    Name = ep.Name,
                    ImageUrl = aggregate.ImageUrl,
                    AiredDate = ep.Aired!.Value,
                    FinaleType = ep.FinaleType,
                    SeriesCaughtUp = seriesCaughtUp
                });
            }

            if (progress.NextUnwatchedEpisode is { } next)
            {
                if (ContinueWatchingOrder.HasRecentPremiere(progress.OrderedEpisodes, today, libraryOptions.Value.PremiereReturnDays))
                    recentPremieres.Add(aggregate.TvdbId);

                // Prefer the still already on the aggregate. Without one, show the
                // series' background art for now — EnrichCatchUpImagesAsync then
                // tries the episode's own record for a screencap.
                var summaryImage = aggregate.Episodes.FirstOrDefault(e => e.Id == next.Id)?.Image;
                if (string.IsNullOrWhiteSpace(summaryImage))
                    summaryImage = null;

                catchUp.Add(new CatchUpItem
                {
                    SeriesId = aggregate.TvdbId,
                    SeriesName = aggregate.Name,
                    EpisodeId = next.Id,
                    SeasonNumber = next.SeasonNumber,
                    EpisodeNumber = next.EpisodeNumber,
                    Name = next.Name,
                    ImageUrl = summaryImage ?? NullIfBlank(aggregate.BackgroundUrl),
                    HasEpisodeImage = summaryImage is not null
                });
            }
        }

        UpcomingEpisodes = [.. upcoming.OrderBy(e => e.AiredDate)];

        // "Continue watching": what the user is in the middle of comes first,
        // and what they haven't touched in a while is left to the Library
        // (ContinueWatchingOrder has both rules, shared with the Library). The
        // unwatched count above still includes those series.
        var lastWatchedBySeries = await watchedRepository.GetLastWatchedUtcBySeriesAsync(userId, cancellationToken);

        var queue = ContinueWatchingOrder.Arrange(
            catchUp,
            c => c.SeriesId,
            c => c.SeriesName,
            lastWatchedBySeries,
            ContinueWatchingOrder.AddedUtc(trackedSeriesIds),
            recentPremieres,
            today,
            libraryOptions.Value);

        var orderedCatchUp = queue.Active.ToList();
        DormantSeriesCount = queue.Dormant.Count;

        CatchUpEpisodes = await EnrichCatchUpImagesAsync(orderedCatchUp, cancellationToken);
        UpcomingThisWeekCount = upcoming.Count(e => e.AiredDate <= thisWeekCutoff);
        UnwatchedCount = unwatchedTotal;
    }

    /// <summary>
    /// Fills in the still for "Continue watching" cards whose aggregate didn't have one:
    /// the episode's own (layered-cached) record — the same source
    /// Episodes/Details uses — sometimes does. A card that already has its
    /// episode's still is left alone, so a library whose next episodes all have
    /// stills costs no per-episode lookups at all.
    /// </summary>
    private async Task<List<CatchUpItem>> EnrichCatchUpImagesAsync(
        List<CatchUpItem> items,
        CancellationToken cancellationToken)
    {
        if (items.All(item => item.HasEpisodeImage))
            return items;

        var episodes = await Task.WhenAll(
            items.Select(item => item.HasEpisodeImage
                ? Task.FromResult<Episode?>(null)
                : TryGetEpisodeAsync(item.EpisodeId, cancellationToken)));

        return items
            .Zip(episodes, (item, episode) =>
                episode?.Image is { } image
                    ? item with { ImageUrl = image }
                    : item)
            .ToList();
    }

    private async Task<Episode?> TryGetEpisodeAsync(int episodeId, CancellationToken cancellationToken)
    {
        try
        {
            return await theTvDbService.GetEpisodeDetailsAsync(episodeId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load episode {EpisodeId} for the home catch-up image.", episodeId);
            return null;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The catch-up card's one-tap "watched" button. There is no confirmation
    /// and the card is gone after the redirect, so the success toast carries an
    /// Undo (the same one a bulk mark offers, for a batch of one).
    /// </summary>
    public async Task<IActionResult> OnPostMarkWatchedAsync(int seriesId, int episodeId, CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId  ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

        try
        {
            var result = await watchProgressService.MarkEpisodeWatchedUndoablyAsync(userId, seriesId, episodeId, cancellationToken);

            switch (result.Outcome)
            {
                case EpisodeWatchOutcome.MarkedWatched when result.Batch is { InsertedCount: > 0 } batch:
                    this.SetSuccessToastWithWatchedUndo(
                        await DescribeMarkedAsync(seriesId, episodeId, cancellationToken),
                        seriesId,
                        batch,
                        undoSingle: true);
                    break;
                case EpisodeWatchOutcome.EpisodeNotInSeries:
                    this.SetErrorToast("That episode doesn't belong to this series.");
                    break;
                case EpisodeWatchOutcome.NotAired:
                    this.SetErrorToast("You can't mark an episode as watched before it has aired.");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed marking episode {EpisodeId} of series {SeriesId} watched from the dashboard.", episodeId, seriesId);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage();
    }

    /// <summary>"Marked Severance S02E06 as watched." — from the (cached) aggregate; a plain sentence when it can't be read.</summary>
    private async Task<string> DescribeMarkedAsync(int seriesId, int episodeId, CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.TryGetSeriesAggregateAsync(seriesId, logger, nameof(DashboardModel), cancellationToken);
        var episode = aggregate?.ToWatchableEpisodes().FirstOrDefault(e => e.Id == episodeId);

        return aggregate is null || episode is null
            ? "Marked as watched."
            : $"Marked {aggregate.Name} {episode.SlateCode()} as watched.";
    }
}
