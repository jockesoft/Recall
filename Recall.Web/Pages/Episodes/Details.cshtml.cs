using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Episodes;

[Authorize]
public sealed class DetailsModel(
    ILogger<DetailsModel> logger,
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    IEpisodeWatchRepository episodeWatchRepository,
    IWatchProgressService watchProgressService,
    ILikeRepository likeRepository,
    IRatingRepository ratingRepository,
    IOmdbApiClient omdbApiClient,
    IEpisodeOmdbSnapshotStore episodeOmdbSnapshotStore,
    IOmdbRequestBudget omdbRequestBudget,
    IOptions<OmdbOptions> omdbOptions)
    : PageModel
{
    /// <summary>Only refresh an episode's OMDb data this rarely — matches UpdateOmdbInfoTimer's series cadence.</summary>
    private static readonly TimeSpan OmdbRefreshAge = TimeSpan.FromDays(30);

    public Episode? Episode { get; set; }

    /// <summary>
    /// The still to render: the episode's own image, or — when TheTVDB hasn't
    /// backfilled that yet — the per-episode image from the series aggregate,
    /// which is sometimes populated first. Same fallback Index/Favorites use.
    /// </summary>
    public string? DisplayImage { get; private set; }

    public bool IsWatchedByCurrentUser { get; private set; }

    /// <summary>Whether the current user has hearted this episode.</summary>
    public bool IsLikedByCurrentUser { get; private set; }

    /// <summary>The current user's 1-10 rating of this episode, or null when unrated.</summary>
    public int? CurrentUserRating { get; private set; }

    /// <summary>Average of every Recall user's rating of this episode, when at least one exists.</summary>
    public double? RecallRatingAverage { get; private set; }

    /// <summary>How many Recall users have rated this episode.</summary>
    public int RecallRatingCount { get; private set; }

    /// <summary>IMDb id for this specific episode, from TheTVDB's remote ids, when known.</summary>
    public string? ImdbId { get; private set; }

    /// <summary>OMDb enrichment for this episode, when available (fetched lazily and cached).</summary>
    public OmdbSeries? Omdb { get; private set; }

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

    public async Task<IActionResult> OnPostToggleLikeAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to like an episode.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            var seriesId = episode?.SeriesId is > 0 ? episode.SeriesId.Value : id;

            await likeRepository.ToggleAsync(userId, LikeTargetType.Episode, id, seriesId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for episode {EpisodeId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRateEpisodeAsync(
        [FromRoute] int id,
        [FromForm] int value,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to rate an episode.");
            return RedirectToPage(new { id });
        }

        if (value is < 1 or > 10)
            return RedirectToPage(new { id });

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            var seriesId = episode?.SeriesId is > 0 ? episode.SeriesId.Value : id;

            await ratingRepository.RateAsync(userId, RatingTargetType.Episode, id, seriesId, value, cancellationToken);
            this.SetSuccessToast("Rating saved.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed rating episode {EpisodeId}.", id);
            this.SetErrorToast("Could not save your rating right now.");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostClearEpisodeRatingAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to rate an episode.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            await ratingRepository.RemoveRatingAsync(userId, RatingTargetType.Episode, id, cancellationToken);
            this.SetInfoToast("Rating removed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed clearing rating for episode {EpisodeId}.", id);
            this.SetErrorToast("Could not update your rating right now.");
        }

        return RedirectToPage(new { id });
    }

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

            if (!result.EpisodeFound)
            {
                logger.LogWarning(
                    "MarkWatchedThroughAsync rejected: episode {EpisodeId} is not part of series {SeriesId}.",
                    id, seriesId);
                this.SetErrorToast("Could not verify this episode against its series right now.");
            }
            else
            {
                this.SetSuccessToast(result.MarkedCount > 1
                    ? $"Marked {result.MarkedCount} episodes as watched."
                    : "Episode marked as watched.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while marking episode {EpisodeId} and earlier episodes as watched.", id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Cache-through OMDb lookup for one episode: serves the cached snapshot when
    /// it's still fresh, otherwise calls OMDb live and stores the result. Unlike
    /// series (enriched proactively by <c>UpdateOmdbInfoTimer</c>), episodes are
    /// only enriched on demand — there are far more of them, so eagerly fetching
    /// every one isn't worth the OMDb quota.
    /// </summary>
    private async Task<OmdbSeries?> LoadOmdbAsync(int episodeTvdbId, string imdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(omdbOptions.Value.ApiKey))
            return await episodeOmdbSnapshotStore.GetAsync(episodeTvdbId, cancellationToken);

        var retrievedUtc = await episodeOmdbSnapshotStore.GetRetrievedUtcAsync(episodeTvdbId, cancellationToken);
        if (retrievedUtc is { } retrieved && DateTime.UtcNow - retrieved < OmdbRefreshAge)
            return await episodeOmdbSnapshotStore.GetAsync(episodeTvdbId, cancellationToken);

        if (!omdbRequestBudget.TryAcquire())
        {
            // The shared daily OMDb budget (also drawn on by UpdateOmdbInfoTimer's
            // proactive series enrichment) is exhausted for today — serve whatever
            // is cached rather than risk pushing the combined total over OMDb's
            // quota and breaking that job too.
            logger.LogInformation(
                "Daily OMDb request budget exhausted; skipping live lookup for episode {EpisodeId}.", episodeTvdbId);
            return await episodeOmdbSnapshotStore.GetAsync(episodeTvdbId, cancellationToken);
        }

        try
        {
            var data = await omdbApiClient.GetByImdbIdAsync(imdbId, "episode", cancellationToken);
            await episodeOmdbSnapshotStore.UpsertAsync(episodeTvdbId, imdbId, data, cancellationToken);
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch OMDb rating for episode {EpisodeId}; using cached snapshot if any.", episodeTvdbId);
            return await episodeOmdbSnapshotStore.GetAsync(episodeTvdbId, cancellationToken);
        }
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
                DisplayImage = Episode.Image;

                if (DateOnly.TryParse(Episode.Aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
                    AiredDate = aired;

                if (Episode.SeriesId is > 0)
                {
                    var aggregate = await LoadSeriesHeaderAsync(Episode.SeriesId.Value, cancellationToken);
                    if (aggregate is not null && Episode.Id is { } currentId)
                    {
                        SetEpisodeNav(aggregate, currentId);

                        DisplayImage ??= aggregate.Episodes.FirstOrDefault(e => e.Id == currentId)?.Image;
                    }
                }

                var ratingSummary = await ratingRepository.GetSummaryAsync(RatingTargetType.Episode, id, cancellationToken);
                RecallRatingAverage = ratingSummary.Average;
                RecallRatingCount = ratingSummary.Count;

                ImdbId = Episode.RemoteIds
                    .FirstOrDefault(r => string.Equals(r.SourceName, "imdb", StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrWhiteSpace(r.Id))
                    ?.Id;

                if (!string.IsNullOrWhiteSpace(ImdbId) && Episode.Id is > 0)
                {
                    Omdb = await LoadOmdbAsync(Episode.Id.Value, ImdbId, cancellationToken);
                }
            }

            if (Episode is not null &&
                currentUserService.IsAuthenticated &&
                !string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
            {
                var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

                WatchedOnUtc = await episodeWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
                IsWatchedByCurrentUser = WatchedOnUtc is not null;

                IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Episode, id, cancellationToken);
                CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Episode, id, cancellationToken);

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
