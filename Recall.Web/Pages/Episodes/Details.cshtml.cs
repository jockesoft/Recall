using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Episodes;

[Authorize]
public sealed class DetailsModel(
    ILogger<DetailsModel> logger,
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    IEpisodeWatchRepository episodeWatchRepository,
    IWatchProgressService watchProgressService)
    : PageModel
{
    public Episode? Episode { get; set; }
    public bool IsWatchedByCurrentUser { get; private set; }

    /// <summary>
    /// How many episodes before this one (by season/episode order) the current
    /// user hasn't marked watched yet. 0 means "nothing to catch up on" — the
    /// view skips the confirmation modal in that case.
    /// </summary>
    public int PriorUnwatchedCount { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
        => await LoadPageAsync(id, cancellationToken);

    public async Task<IActionResult> OnPostToggleWatchedAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            if (episode is null)
                return NotFound();

            if (episode.SeriesId is null or <= 0)
            {
                this.SetErrorToast("Episode does not have a valid series reference.");
                return RedirectToPage(new { id });
            }

            var isWatched = await episodeWatchRepository.IsWatchedAsync(userId, id, cancellationToken);

            if (isWatched)
            {
                await episodeWatchRepository.MarkUnwatchedAsync(userId, id, cancellationToken);
                this.SetInfoToast("Episode marked as not watched.");
            }
            else
            {
                await episodeWatchRepository.MarkWatchedAsync(userId, episode.SeriesId.Value, id, cancellationToken);
                this.SetSuccessToast("Episode marked as watched.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while toggling watched status for episode {EpisodeId}.", id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Marks the given episode AND every earlier episode in the same series
    /// (by season/episode order) as watched, skipping ones already watched.
    /// </summary>
    public async Task<IActionResult> OnPostMarkWatchedThroughAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            if (episode is null)
                return NotFound();

            if (episode.SeriesId is null or <= 0)
            {
                this.SetErrorToast("Episode does not have a valid series reference.");
                return RedirectToPage(new { id });
            }

            var seriesId = episode.SeriesId.Value;

            var result = await watchProgressService.MarkWatchedThroughAsync(userId, seriesId, id, cancellationToken);

            this.SetSuccessToast(result.MarkedCount > 1
                ? $"Marked {result.MarkedCount} episodes as watched."
                : "Episode marked as watched.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while marking episode {EpisodeId} and earlier episodes as watched.", id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> LoadPageAsync(int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);

            if (Episode is not null &&
                currentUserService.IsAuthenticated &&
                !string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
            {
                var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

                IsWatchedByCurrentUser = await episodeWatchRepository.IsWatchedAsync(userId, id, cancellationToken);

                if (!IsWatchedByCurrentUser && Episode.SeriesId is > 0)
                {
                    PriorUnwatchedCount = await watchProgressService.GetPriorUnwatchedCountAsync(userId, Episode.SeriesId.Value, id, cancellationToken);
                }
            }

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
}
