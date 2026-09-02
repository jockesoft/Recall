using System.Globalization;
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

    /// <summary>When the current user marked this episode watched, if they have.</summary>
    public DateTime? WatchedOnUtc { get; private set; }

    /// <summary>Name of the series this episode belongs to (for the header link).</summary>
    public string? SeriesName { get; private set; }

    /// <summary>Series slug, used to build the TheTVDB episode link.</summary>
    public string? SeriesSlug { get; private set; }

    /// <summary>Parsed air date, when the episode has one.</summary>
    public DateOnly? AiredDate { get; private set; }

    /// <summary>Series broadcast time in its home timezone, e.g. "20:00". May be null.</summary>
    public string? AirsTime { get; private set; }

    /// <summary>
    /// True unless the episode has a known air date that is still in the future.
    /// Drives "Aired" vs "Airs" wording and whether the watched button is enabled.
    /// </summary>
    public bool HasAired =>
        AiredDate is not { } aired || aired <= DateOnly.FromDateTime(DateTime.Today);

    /// <summary>
    /// How many episodes before this one (by season/episode order) the current
    /// user hasn't marked watched yet. 0 means "nothing to catch up on" — the
    /// view skips the confirmation modal in that case.
    /// </summary>
    public int PriorUnwatchedCount { get; private set; }

    /// <summary>
    /// Previous / next episode in season/episode order, for the page-foot nav.
    /// Null when the current episode sits at that end of the series (or isn't a
    /// tracked, non-movie episode present in the aggregate).
    /// </summary>
    public EpisodeNavLink? PreviousEpisode { get; private set; }

    public EpisodeNavLink? NextEpisode { get; private set; }

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

    /// <summary>
    /// Pulls the series name (for the header link) and its broadcast time from
    /// the layered-cached series aggregate — the same snapshot the background
    /// refresh timer keeps current, so no extra TheTVDB call is made here.
    /// Best-effort: a failure must not break the episode page.
    /// </summary>
    private async Task<SeriesAggregate?> LoadSeriesHeaderAsync(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            var aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(seriesId, cancellationToken);
            SeriesName = aggregate?.Name;
            SeriesSlug = aggregate?.Slug;
            AirsTime = aggregate?.AirsTime;
            return aggregate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load series {SeriesId} for the episode header.", seriesId);
            return null;
        }
    }

    /// <summary>
    /// Fills <see cref="PreviousEpisode"/> / <see cref="NextEpisode"/> from the
    /// series aggregate, using the same season/episode ordering as the rest of
    /// the app. Leaves them null when the current episode isn't in the ordered
    /// list or has no neighbour on that side.
    /// </summary>
    private void SetEpisodeNav(SeriesAggregate aggregate, int currentEpisodeId)
    {
        var ordered = WatchProgressCalculator.Order(aggregate.ToWatchableEpisodes());

        var index = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id == currentEpisodeId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        if (index > 0)
            PreviousEpisode = ToNavLink(ordered[index - 1]);

        if (index < ordered.Count - 1)
            NextEpisode = ToNavLink(ordered[index + 1]);

        static EpisodeNavLink ToNavLink(WatchableEpisode e) =>
            new(e.Id, e.SeasonNumber, e.EpisodeNumber);
    }

    private async Task<IActionResult> LoadPageAsync(int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);

            if (Episode is not null)
            {
                if (DateOnly.TryParse(Episode.Aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
                    AiredDate = aired;

                if (Episode.SeriesId is > 0)
                {
                    var aggregate = await LoadSeriesHeaderAsync(Episode.SeriesId.Value, cancellationToken);
                    if (aggregate is not null && Episode.Id is { } currentId)
                        SetEpisodeNav(aggregate, currentId);
                }
            }

            if (Episode is not null &&
                currentUserService.IsAuthenticated &&
                !string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
            {
                var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

                WatchedOnUtc = await episodeWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
                IsWatchedByCurrentUser = WatchedOnUtc is not null;

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

/// <summary>Target for a prev/next episode nav button on the episode detail page.</summary>
public sealed record EpisodeNavLink(int Id, int? SeasonNumber, int? EpisodeNumber)
{
    /// <summary>
    /// "S04 · E03"-style label, or null when either number is missing (the nav
    /// button then shows just its "Prev/Next episode" line).
    /// </summary>
    public string? SlateCode =>
        SeasonNumber is { } s && EpisodeNumber is { } e
            ? $"S{s:D2} · E{e:D2}"
            : null;
}
