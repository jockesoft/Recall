using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Series;

public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    IEpisodeWatchRepository episodeWatchRepository,
    ILogger<DetailsModel> logger)
    : PageModel
{
    public TvSeriesDetails? Series { get; private set; }
    public SeriesAggregate? Aggregate { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int? Season { get; set; }

    public bool IsTrackedByCurrentUser { get; private set; }
    public IReadOnlySet<int> WatchedEpisodeIds { get; private set; } = new HashSet<int>();

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
        => await LoadPageAsync(id, cancellationToken);

    public async Task<IActionResult> OnPostToggleLibraryAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to manage your library.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            
            await AddToPersonalLibraryAsync(userId, id, onlyAdd: false, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in your library", StringComparison.OrdinalIgnoreCase))
        {
            this.SetInfoToast("Series is already in your library.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your library right now.");
        }

        return RedirectToPage(new { id, season = Season });
    }

    /// <summary>
    /// Checks how many unwatched episodes come before the given episode in series order.
    /// Returns JSON so the view can conditionally show a "catch up" modal.
    /// </summary>
    public async Task<IActionResult> OnPostCheckPriorEpisodesAsync(
        [FromRoute] int id,
        [FromForm] int episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId <= 0)
            return BadRequest();

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
            return Unauthorized();

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            var ordered = await GetOrderedEpisodesAsync(id, cancellationToken);
            var episode = ordered.FirstOrDefault(e => e.Id == episodeId);
            if (episode is null)
                return NotFound();

            var priorUnwatchedCount = await GetPriorUnwatchedCountAsync(userId, id, episodeId, ordered, cancellationToken);

            return new JsonResult(new { priorUnwatchedCount });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed checking prior episodes for series {SeriesId}, episode {EpisodeId}.", id, episodeId);
            return StatusCode(500);
        }
    }


    public async Task<IActionResult> OnPostToggleEpisodeWatchedAsync(
        [FromRoute] int id,
        [FromForm] int episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId <= 0)
            return RedirectToPage(new { id, season = Season });

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            var isWatched = await episodeWatchRepository.IsWatchedAsync(userId, episodeId, cancellationToken);

            if (isWatched)
            {
                await episodeWatchRepository.MarkUnwatchedAsync(userId, episodeId, cancellationToken);
                this.SetInfoToast("Episode marked as not watched.");
            }
            else
            {
                // Make sure the series is in the users library, otherwise why track progress
                await AddToPersonalLibraryAsync(userId, id, onlyAdd: true, cancellationToken);
                await episodeWatchRepository.MarkWatchedAsync(userId, id, episodeId, cancellationToken);
                this.SetSuccessToast("Episode marked as watched.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling watched state for series {SeriesId}, episode {EpisodeId}.", id, episodeId);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id, season = Season });
    }

    /// <summary>
    /// Marks the given episode AND every earlier episode in the same series
    /// (by season/episode order) as watched, skipping ones already watched.
    /// </summary>
    public async Task<IActionResult> OnPostMarkWatchedThroughAsync(
        [FromRoute] int id,
        [FromForm] int episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId <= 0)
            return RedirectToPage(new { id, season = Season });

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            // Make sure the series is in the users library, otherwise why track progress
            await AddToPersonalLibraryAsync(userId, id, onlyAdd: true, cancellationToken);
            
            var ordered = await GetOrderedEpisodesAsync(id, cancellationToken);
            var episode = ordered.FirstOrDefault(e => e.Id == episodeId);
            if (episode is null)
                return RedirectToPage(new { id, season = Season });

            var currentIndex = ordered.FindIndex(e => e.Id == episode.Id);

            var idsToMark = currentIndex >= 0
                ? ordered.Take(currentIndex + 1).Select(e => e.Id).ToList()
                : [episode.Id];

            await episodeWatchRepository.MarkWatchedRangeAsync(userId, id, idsToMark, cancellationToken);

            this.SetSuccessToast(idsToMark.Count > 1
                ? $"Marked {idsToMark.Count} episodes as watched."
                : "Episode marked as watched.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking episode {EpisodeId} and earlier episodes as watched for series {SeriesId}.", episodeId, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id, season = Season });
    }

    private async Task<IActionResult> LoadPageAsync(int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(id, cancellationToken);
            if (Aggregate is null) return NotFound();

            Series = new TvSeriesDetails(
                Aggregate.TvdbId,
                Aggregate.Name,
                Aggregate.Slug,
                Aggregate.Overview,
                Aggregate.ImageUrl,
                Aggregate.FirstAired?.ToString("yyyy-MM-dd"),
                Aggregate.Score);

            if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
                return Page();

            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            IsTrackedByCurrentUser = await trackedSeriesRepository.ExistsAsync(userId, id, cancellationToken);
            WatchedEpisodeIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, [id], cancellationToken);
            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading details for id {SeriesId}.", id);
            this.SetErrorToast("Could not fetch series details from TheTVDB right now.");
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while loading details for id {SeriesId}.", id);
            this.SetErrorToast("An unexpected error occurred.");
            return Page();
        }
    }

    public string GetSeasonName(int season)
    {
        if (season == 0) return "Specials";
        else return "Season " + season;
    }

    /// <summary>
    /// Counts unwatched episodes strictly before <paramref name="currentEpisodeId"/>
    /// in season/episode order. Fails closed (returns 0, no modal shown) rather
    /// than letting a transient error here block the whole page.
    /// </summary>
    private async Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesId,
        int currentEpisodeId,
        IReadOnlyList<EpisodeSummary> ordered,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentIndex = ordered.ToList().FindIndex(e => e.Id == currentEpisodeId);
            if (currentIndex <= 0)
                return 0;

            var priorIds = ordered.Take(currentIndex).Select(e => e.Id).ToList();
            if (priorIds.Count == 0)
                return 0;

            var watchedIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, [seriesId], cancellationToken);

            return priorIds.Count(pid => !watchedIds.Contains(pid));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not compute prior-unwatched count for series {SeriesId}.", seriesId);
            return 0;
        }
    }

    /// <summary>
    /// Gets episodes in season/episode order, excluding movies.
    /// </summary>
    private async Task<List<EpisodeSummary>> GetOrderedEpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(seriesId, cancellationToken);
        var episodes = aggregate?.Episodes ?? [];

        return episodes
            .Where(e => e.IsMovie != true)
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ToList();
    }

    private async Task AddToPersonalLibraryAsync(
        Guid userId,
        int seriesId,
        bool onlyAdd,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(userId, seriesId, cancellationToken);

            if (existing is null)
            {
                var series = await theTvDbService.GetSeriesByIdAsync(seriesId, cancellationToken);
                if (series is null) return;

                var tracked = TrackedSeriesMappings.FromTvDbDetails(userId, series);
                await trackedSeriesRepository.AddAsync(tracked, cancellationToken);
                this.SetSuccessToast("Series saved to your library.");
            }
            else
            {
                if (!onlyAdd)
                {
                    await trackedSeriesRepository.RemoveAsync(userId, existing.Id, cancellationToken);
                    this.SetInfoToast("Series removed from your library.");
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in your library", StringComparison.OrdinalIgnoreCase))
        {
            this.SetInfoToast("Series is already in your library.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", seriesId);
            this.SetErrorToast("Could not update your library right now.");
        }   
    }
}