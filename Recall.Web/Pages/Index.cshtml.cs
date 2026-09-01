using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
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
}

public sealed record CatchUpItem
{
    public int SeriesId { get; init; }
    public string SeriesName { get; init; } = "";
    public int EpisodeId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Still for the next episode (full URL); falls back to the series cover.</summary>
    public string? ImageUrl { get; init; }
}

// ---------------------------------------------------------------------------
// Page model
// ---------------------------------------------------------------------------

[Authorize]
public sealed class IndexModel(
    ITheTvDbService theTvDbService,
    ITrackedSeriesRepository libraryRepository,
    IEpisodeWatchRepository watchedRepository,
    IWatchProgressService watchProgressService,
    ILogger<IndexModel> logger,
    ICurrentUserService currentUserService) : PageModel
{
    private const int UpcomingWindowDays = 30;
    private const int ThisWeekWindowDays = 7;

    public int TrackedSeriesCount { get; private set; }
    public int UpcomingThisWeekCount { get; private set; }
    public int UnwatchedCount { get; private set; }
    public List<UpcomingEpisodeItem> UpcomingEpisodes { get; private set; } = [];
    public List<CatchUpItem> CatchUpEpisodes { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
        var trackedSeriesIds = await libraryRepository.GetByUserAsync(userId, cancellationToken);
        TrackedSeriesCount = trackedSeriesIds.Count;

        if (trackedSeriesIds.Count == 0)
            return;

        var aggregates = (await Task.WhenAll(
                trackedSeriesIds.Select(id => TryGetAggregateAsync(id.TvdbId, cancellationToken))))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        var seriesIds = aggregates.Select(a => a.TvdbId).ToList();
        var watchedIds = await watchedRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var upcomingCutoff = today.AddDays(UpcomingWindowDays);
        var thisWeekCutoff = today.AddDays(ThisWeekWindowDays);

        var upcoming = new List<UpcomingEpisodeItem>();
        var catchUp = new List<CatchUpItem>();
        var unwatchedTotal = 0;

        foreach (var aggregate in aggregates)
        {
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
                    AiredDate = ep.Aired!.Value
                });
            }

            // Next episode to watch + unwatched count: shared logic, same rule as
            // the series page (earliest aired episode not marked watched).
            var progress = watchProgressService.BuildProgress(aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedIds);
            unwatchedTotal += progress.UnwatchedReleasedCount;

            if (progress.NextUnwatchedEpisode is { } next)
            {
                // Prefer any still already on the aggregate; fall back to the
                // series cover for now — EnrichCatchUpImagesAsync then swaps in
                // the real per-episode screencap from the episode endpoint.
                var summaryImage = aggregate.Episodes
                    .FirstOrDefault(e => e.Id == next.Id)?.Image;

                catchUp.Add(new CatchUpItem
                {
                    SeriesId = aggregate.TvdbId,
                    SeriesName = aggregate.Name,
                    EpisodeId = next.Id,
                    SeasonNumber = next.SeasonNumber,
                    EpisodeNumber = next.EpisodeNumber,
                    Name = next.Name,
                    ImageUrl = string.IsNullOrWhiteSpace(summaryImage) ? aggregate.ImageUrl : summaryImage
                });
            }
        }

        UpcomingEpisodes = [.. upcoming.OrderBy(e => e.AiredDate)];

        // Order "Catch up" by how recently the user last watched an episode of
        // that series (most recent first) — the show you're mid-binge on floats
        // to the top, where its next episode is the most likely thing you want.
        // Series with nothing watched yet fall to the end, alphabetically.
        var lastWatchedBySeries = await watchedRepository.GetLastWatchedUtcBySeriesAsync(
            userId, catchUp.Select(c => c.SeriesId), cancellationToken);

        var orderedCatchUp = catchUp
            .OrderByDescending(c => lastWatchedBySeries.TryGetValue(c.SeriesId, out var watchedUtc)
                ? watchedUtc
                : DateTime.MinValue)
            .ThenBy(c => c.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CatchUpEpisodes = await EnrichCatchUpImagesAsync(orderedCatchUp, cancellationToken);
        UpcomingThisWeekCount = upcoming.Count(e => e.AiredDate <= thisWeekCutoff);
        UnwatchedCount = unwatchedTotal;
    }

    /// <summary>
    /// The series-extended payload doesn't carry per-episode stills, so the
    /// "Catch up" cards would otherwise show the series poster. Fetch each next
    /// episode from the (layered-cached) episode endpoint — the same source
    /// Episodes/Details uses — and swap in its screencap when it has one.
    /// </summary>
    private async Task<List<CatchUpItem>> EnrichCatchUpImagesAsync(
        List<CatchUpItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return items;

        var episodes = await Task.WhenAll(
            items.Select(item => TryGetEpisodeAsync(item.EpisodeId, cancellationToken)));

        return items
            .Zip(episodes, (item, episode) =>
                string.IsNullOrWhiteSpace(episode?.Image)
                    ? item
                    : item with { ImageUrl = episode.Image })
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

    public async Task<IActionResult> OnPostMarkWatchedAsync(int seriesId, int episodeId, CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId  ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
        await watchedRepository.MarkWatchedAsync(userId, seriesId, episodeId, cancellationToken);
        return RedirectToPage();
    }

    /// <summary>
    /// Wraps a single series' aggregate fetch so one series failing upstream
    /// (timeout, deserialization error, etc.) doesn't take down the whole
    /// dashboard for every other tracked series.
    /// </summary>
    private async Task<SeriesAggregate?> TryGetAggregateAsync(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            return await theTvDbService.GetSeriesAggregateByIdAsync(seriesId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load series aggregate {SeriesId} for home dashboard.", seriesId);
            return null;
        }
    }
}
