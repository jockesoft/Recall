using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Episodes;

public sealed class DetailsModel(
    ILogger<DetailsModel> logger,
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    IEpisodeWatchRepository episodeWatchRepository)
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

            var ordered = await GetOrderedEpisodesAsync(seriesId, cancellationToken);

            var currentIndex = ordered.FindIndex(e => e.Id == episode.Id);
            
/*            var idsToMark = currentIndex >= 0
                ? ordered.Take(currentIndex + 1).Select(e => e.Id).ToList()
                : (List<int>)[episode.Id!.Value];*/

            var idsToMarkWithNull = currentIndex >= 0
                ? ordered.Take(currentIndex + 1).Select(e => e.Id).ToList()
                : (List<int?>)[episode.Id!.Value];

            var idsToMark = idsToMarkWithNull
                .OfType<int>()
                .ToList();
            
            await episodeWatchRepository.MarkWatchedRangeAsync(userId, seriesId, idsToMark, cancellationToken);

            this.SetSuccessToast(idsToMark.Count > 1
                ? $"Marked {idsToMark.Count} episodes as watched."
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
                    PriorUnwatchedCount = await GetPriorUnwatchedCountAsync(userId, Episode.SeriesId.Value, Episode, cancellationToken);
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

    /// <summary>
    /// Counts unwatched episodes strictly before <paramref name="currentEpisode"/>
    /// in season/episode order. Fails closed (returns 0, no modal shown) rather
    /// than letting a transient error here block the whole page — this is a
    /// nice-to-have prompt, not something worth a broken page over.
    /// </summary>
    private async Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesId,
        Episode currentEpisode,
        CancellationToken cancellationToken)
    {
        try
        {
            var ordered = await GetOrderedEpisodesAsync(seriesId, cancellationToken);

            var currentIndex = ordered.FindIndex(e => e.Id == currentEpisode.Id);
            if (currentIndex <= 0)
                return 0;

            var priorIds = ordered.Take(currentIndex).Select(e => e.Id).ToList();
            if (priorIds.Count == 0)
                return 0;

            var watchedIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesId, cancellationToken);

            return priorIds.Count(pid => !watchedIds.Contains(pid.Value));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not compute prior-unwatched count for episode {EpisodeId}.", currentEpisode.Id);
            return 0;
        }
    }

    /// <summary>
    /// Single source of truth for "the ordered, non-movie episode list for a series."
    /// Used by both the count shown in the confirmation modal and the actual
    /// mark-through action — kept in one place so those two can never disagree
    /// about which episodes count as "earlier."
    /// </summary>
/*    private async Task<List<EpisodeSummary>> GetOrderedEpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var serie = await theTvDbService.GetSeriesAggregateByIdAsync(seriesId, cancellationToken);
        var episodes = serie?.Episodes ?? [];

        return episodes
            .Where(e => e.IsMovie != true) // null/unset treated as "not a movie", never throws
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ToList();
    }*/
    private async Task<List<Episode>> GetOrderedEpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var serie = await theTvDbService.GetSeriesByIdExtendedAsync(seriesId, cancellationToken);
        var episodes = serie?.Episodes ?? [];

        return episodes
            .Where(e => !e.IsMovie)
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.AbsoluteNumber ?? int.MaxValue)
            .ToList();
    }
}
