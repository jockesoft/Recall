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
            var existing = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(userId, id, cancellationToken);

            if (existing is null)
            {
                var series = await theTvDbService.GetSeriesByIdAsync(id, cancellationToken);
                if (series is null) return NotFound();

                var tracked = TrackedSeriesMappings.FromTvDbDetails(userId, series);
                await trackedSeriesRepository.AddAsync(tracked, cancellationToken);
                this.SetSuccessToast("Series saved to your library.");
            }
            else
            {
                await trackedSeriesRepository.RemoveAsync(userId, existing.Id, cancellationToken);
                this.SetInfoToast("Series removed from your library.");
            }
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
            WatchedEpisodeIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, id, cancellationToken);
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
}