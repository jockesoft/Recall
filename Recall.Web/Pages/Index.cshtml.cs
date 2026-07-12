using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

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

public sealed class CatchUpItem
{
    public int SeriesId { get; init; }
    public string SeriesName { get; init; } = "";
    public int EpisodeId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = "";
    public string? ImageUrl { get; init; }
}

// ---------------------------------------------------------------------------
// Page model
// ---------------------------------------------------------------------------

[Authorize]
public sealed class IndexModel(
    ITheTvDbApiClient tvdbClient,
    ITrackedSeriesRepository libraryRepository,
    IEpisodeWatchRepository watchedRepository,
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

        var allEpisodeIds = aggregates.SelectMany(a => a.Episodes).Select(e => e.Id).ToList();
        var watchedIds = await watchedRepository.GetWatchedEpisodeIdsAsync(userId, allEpisodeIds[0], cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var upcomingCutoff = today.AddDays(UpcomingWindowDays);
        var thisWeekCutoff = today.AddDays(ThisWeekWindowDays);

        var upcoming = new List<UpcomingEpisodeItem>();
        var catchUp = new List<CatchUpItem>();
        var unwatchedTotal = 0;

        foreach (var aggregate in aggregates)
        {
            var orderedEpisodes = aggregate.Episodes
                .Where(e => e.Aired.HasValue)
                .OrderBy(e => e.Aired)
                .ThenBy(e => e.SeasonNumber ?? int.MaxValue)
                .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
                .ToList();

            foreach (var ep in orderedEpisodes)
            {
                var aired = ep.Aired!.Value;

                if (aired >= today && aired <= upcomingCutoff)
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
                        AiredDate = aired
                    });
                }

                // Strictly before today, so an episode airing today shows up
                // in Upcoming only — never duplicated into Catch up.
                if (aired < today && !watchedIds.Contains(ep.Id))
                {
                    unwatchedTotal++;
                }
            }

            var nextUnwatched = orderedEpisodes.FirstOrDefault(e => e.Aired!.Value < today && !watchedIds.Contains(e.Id));
            if (nextUnwatched is not null)
            {
                catchUp.Add(new CatchUpItem
                {
                    SeriesId = aggregate.TvdbId,
                    SeriesName = aggregate.Name,
                    EpisodeId = nextUnwatched.Id,
                    SeasonNumber = nextUnwatched.SeasonNumber,
                    EpisodeNumber = nextUnwatched.EpisodeNumber,
                    Name = nextUnwatched.Name,
                    ImageUrl = aggregate.ImageUrl
                });
            }
        }

        UpcomingEpisodes = upcoming.OrderBy(e => e.AiredDate).ToList();
        CatchUpEpisodes = catchUp;
        UpcomingThisWeekCount = upcoming.Count(e => e.AiredDate <= thisWeekCutoff);
        UnwatchedCount = unwatchedTotal;
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
            return await tvdbClient.GetSeriesAggregateByIdAsync(seriesId, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load series aggregate {SeriesId} for home dashboard.", seriesId);
            return null;
        }
    }
}
