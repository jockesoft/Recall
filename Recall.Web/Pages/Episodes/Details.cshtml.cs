using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
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

/// <summary>
/// Public, anonymous-friendly episode details page. Watched/like/rating actions
/// are only shown and only take effect when signed in.
/// </summary>
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.PublicDetailsPolicy)]
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
    IOptions<OmdbOptions> omdbOptions,
    TimeProvider timeProvider)
    : PageModel
{
    /// <summary>Only refresh an episode's OMDb data this rarely — matches UpdateOmdbInfoTimer's series cadence.</summary>
    private static readonly TimeSpan OmdbRefreshAge = TimeSpan.FromDays(30);

    public Episode? Episode { get; set; }

    public bool IsAuthenticated => currentUserService.IsAuthenticated;

    /// <summary>
    /// The image at the top of the page, by the rule the Dashboard and
    /// Favorites share (<see cref="EpisodeArt.Resolve"/>): the episode's still,
    /// else the series' background art, else nothing and the page shows its
    /// placeholder. TheTVDB has no still for many episodes.
    /// </summary>
    public EpisodeArt Art { get; private set; } = EpisodeArt.None;

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
    public OmdbEpisode? Omdb { get; private set; }

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

    /// <summary>The series' country of origin, for the release moment's time zone.</summary>
    public string? SeriesCountry { get; private set; }

    /// <summary>When the episode is released (<see cref="EpisodeRelease"/>); null without an air date.</summary>
    public ReleaseMoment? Release => EpisodeRelease.MomentUtc(AiredDate, AirsTime, SeriesCountry);

    /// <summary>Released by now: "Aired" rather than "Airs". An episode with no air date counts as released.</summary>
    public bool HasAired => Release is not { } release || release.IsReleasedBy(AirDate.Now(timeProvider));

    /// <summary>
    /// Whether the watched button is offered: from the air date anywhere on
    /// Earth, so it can be marked as soon as it has been seen, even a few hours
    /// before it counts as released here (<see cref="AirDate.MayBeMarked"/>).
    /// </summary>
    public bool MayBeMarked => AirDate.MayBeMarked(AiredDate, AirDate.Now(timeProvider));

    /// <summary>Today's date in UTC, for the air-date check and the view's date format.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

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

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "like an episode");

        try
        {
            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            if (episode?.SeriesId is not > 0)
            {
                // No parent series to record this against — refuse rather than
                // store the episode's own id in the series column.
                this.SetErrorToast("Could not verify this episode against its series right now.");
                return RedirectToPage(new { id });
            }

            await likeRepository.ToggleAsync(userId, LikeTargetType.Episode, id, episode.SeriesId.Value, cancellationToken);
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

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate an episode");

        if (value is < 1 or > 10)
            return RedirectToPage(new { id });

        try
        {
            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            if (episode?.SeriesId is not > 0)
            {
                // No parent series to record this against — refuse rather than
                // store the episode's own id in the series column.
                this.SetErrorToast("Could not verify this episode against its series right now.");
                return RedirectToPage(new { id });
            }

            await ratingRepository.RateAsync(userId, RatingTargetType.Episode, id, episode.SeriesId.Value, value, cancellationToken);
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

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate an episode");

        try
        {
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

    /// <summary>"S01E03" for the toast; "the episode" when TheTVDB has no numbers for it.</summary>
    private static string SlateCode(Episode episode) =>
        episode is { SeasonNumber: { } season, Number: { } number } ? $"S{season:D2}E{number:D2}" : "the episode";

    public async Task<IActionResult> OnPostToggleWatchedAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        try
        {
            var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
            if (episode is null)
                return NotFound();

            if (episode.SeriesId is null or <= 0)
            {
                this.SetErrorToast("Episode does not have a valid series reference.");
                return RedirectToPage(new { id });
            }

            var toggled = await watchProgressService.ToggleEpisodeWatchedAsync(userId, episode.SeriesId.Value, id, cancellationToken);
            switch (toggled.Outcome)
            {
                case EpisodeWatchOutcome.MarkedUnwatched:
                    this.SetInfoToast("Episode marked as not watched.");
                    break;
                case EpisodeWatchOutcome.MarkedWatched:
                    // Marking from here puts the series in the library too, as on the series page; say so.
                    this.SetWatchedToast(
                        toggled.AddedToLibrary is { } added
                            ? $"Marked {SlateCode(episode)} as watched and added {added} to your library."
                            : "Episode marked as watched.",
                        toggled.CaughtUp, Today, keepMessage: toggled.AddedToLibrary is not null);
                    break;
                case EpisodeWatchOutcome.NotAired:
                    this.SetErrorToast("You can't mark an episode as watched before it has aired.");
                    break;
                case EpisodeWatchOutcome.EpisodeNotInSeries:
                    this.SetErrorToast("Could not verify this episode against its series right now.");
                    break;
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

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        try
        {
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
            else if (!result.HasAired)
            {
                this.SetErrorToast("You can't mark an episode as watched before it has aired.");
            }
            else
            {
                var marked = result.MarkedCount > 1 ? $"Marked {result.MarkedCount} episodes" : $"Marked {SlateCode(episode)}";
                this.SetSuccessToastWithWatchedUndo(
                    result.AddedToLibrary is { } added
                        ? $"{marked} as watched and added {added} to your library."
                        : result.MarkedCount > 1 ? $"{marked} as watched." : "Episode marked as watched.",
                    seriesId,
                    result.Batch,
                    caughtUp: result.CaughtUp,
                    today: Today);
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
    /// <summary>
    /// What every POST handler here returns to an anonymous caller: the page is
    /// public, so <c>[Authorize]</c> can't guard its handlers.
    /// </summary>
    private IActionResult SignInRequired(int id, string toDoWhat)
    {
        this.SetErrorToast($"You need to be signed in to {toDoWhat}.");
        return RedirectToPage(new { id });
    }

    private async Task<OmdbEpisode?> LoadOmdbAsync(int episodeTvdbId, string imdbId, CancellationToken cancellationToken)
    {
        // Anonymous visitors (and crawlers — the sitemap lists every cached
        // episode) only ever see what is already cached. A live lookup spends
        // the shared daily OMDb budget, and that is reserved for signed-in
        // users and the enrichment jobs.
        if (!currentUserService.IsAuthenticated)
            return await episodeOmdbSnapshotStore.GetAsync(episodeTvdbId, cancellationToken);

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
            var data = await omdbApiClient.GetEpisodeAsync(imdbId, cancellationToken);
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
            SeriesCountry = aggregate?.OriginalCountry;
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

    /// <summary>
    /// When the episode could not be shown: the series the visitor came from
    /// (read from the Referer, since without the episode nothing else says
    /// which series it belongs to), so the page can offer a way back.
    /// </summary>
    public int? CameFromSeriesId
    {
        get
        {
            if (!Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
                || !string.Equals(referer.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase))
                return null;

            const string prefix = "/Series/Details/";
            var path = referer.AbsolutePath;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(path[prefix.Length..].Trim('/'), out var seriesId)
                   && seriesId > 0
                ? seriesId
                : null;
        }
    }

    /// <summary>
    /// True when the episode is missing because TheTVDB (or something else)
    /// failed, rather than because there is no such episode.
    /// </summary>
    public bool LoadFailed { get; private set; }

    private async Task<IActionResult> LoadPageAsync(int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);

            if (Episode is not null)
            {
                // Until the series is loaded, the episode's own record is all there is.
                Art = EpisodeArt.Resolve(series: null, id, Episode);

                if (DateOnly.TryParse(Episode.Aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
                    AiredDate = aired;

                if (Episode.SeriesId is > 0)
                {
                    var aggregate = await LoadSeriesHeaderAsync(Episode.SeriesId.Value, cancellationToken);
                    if (aggregate is not null && Episode.Id is { } currentId)
                    {
                        SetEpisodeNav(aggregate, currentId);
                        Art = EpisodeArt.Resolve(aggregate, currentId, Episode);
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

            if (Episode is not null && currentUserService.TryGetUserId(out var userId))
            {
                WatchedOnUtc = await episodeWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
                IsWatchedByCurrentUser = WatchedOnUtc is not null;

                IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Episode, id, cancellationToken);
                CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Episode, id, cancellationToken);

                if (!IsWatchedByCurrentUser && Episode.SeriesId is > 0)
                {
                    PriorUnwatchedCount = await watchProgressService.GetPriorUnwatchedCountAsync(userId, Episode.SeriesId.Value, id, cancellationToken);
                }
            }

            // The page renders its own "no such episode" state (with a way back
            // to the series) rather than the generic 404 page.
            if (Episode is null)
                Response.StatusCode = StatusCodes.Status404NotFound;

            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading episode {EpisodeId}.", id);
            return Failed(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while loading episode {EpisodeId}.", id);
            return Failed(StatusCodes.Status500InternalServerError);
        }
    }

    private IActionResult Failed(int statusCode)
    {
        Episode = null;
        LoadFailed = true;
        Response.StatusCode = statusCode;
        return Page();
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
