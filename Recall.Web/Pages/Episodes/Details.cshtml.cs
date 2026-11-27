using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Episodes;

/// <summary>
/// Public, anonymous-friendly episode details page, built like Series and
/// Movie Details: the shared hero (<c>_TitleHero</c>, with the episode's still
/// behind it, its series above the title and its place in the season) and
/// action row (<c>_TitleActions</c>), the next episode under it
/// (<c>_UpNextCard</c>), then the panel: the overview and its facts, the guest
/// stars, and the previous and next episodes. Watched/like/rating actions are
/// only shown and only take effect when signed in. Everything comes from the
/// episode's own record and the series aggregate the page already reads, so
/// no TheTVDB request is added (the next episode's record is read from the
/// caches only).
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

    /// <summary>The series aggregate the episode belongs to; null when it could not be read.</summary>
    public SeriesAggregate? Series { get; private set; }

    /// <summary>Now in UTC, for "released" (EpisodeRelease) and "may be marked" (AirDate.MayBeMarked).</summary>
    public DateTime Now => AirDate.Now(timeProvider);

    /// <summary>
    /// Where the episode sits in its season (<see cref="TitlePositionModel"/>):
    /// "Episode 1 of 21 · Season 52", or "Special 3 of 4". Null when the series
    /// could not be read or the episode has no season.
    /// </summary>
    public TitlePositionModel? Position { get; private set; }

    /// <summary>
    /// The episode after this one in watch order (specials last), as the card
    /// under the action row. Null after the last episode.
    /// </summary>
    public UpNextModel? UpNext { get; private set; }

    /// <summary>The episode's guests, from its own cached record (<see cref="CastBuilder.GuestStars"/>).</summary>
    public IReadOnlyList<CastPerson> GuestStars { get; private set; } = [];

    /// <summary>The episode's runtime in minutes: its record's, else the aggregate's.</summary>
    public int? RuntimeMinutes { get; private set; }

    /// <summary>IMDb's rating from the OMDb snapshot ("7.3"); null when unknown.</summary>
    public string? ImdbRating => OmdbValue(Omdb?.ImdbRating);

    /// <summary>IMDb's vote count from the OMDb snapshot ("1,234").</summary>
    public string? ImdbVotes => OmdbValue(Omdb?.ImdbVotes);

    /// <summary>"Season finale", "Series finale" or "Midseason finale", as a chip in the hero.</summary>
    public string? FinaleText => Episode?.FinaleType?.ToLowerInvariant() switch
    {
        "series" => "Series finale",
        "season" => "Season finale",
        "midseason" => "Midseason finale",
        _ => null
    };

    /// <summary>"S52 · E1", "Special · E3", "Movie"; null without numbers.</summary>
    public string? MetaCode => Episode switch
    {
        null => null,
        { IsMovie: true } => "Movie",
        { SeasonNumber: 0, Number: { } special } => $"Special · E{special.ToString(CultureInfo.InvariantCulture)}",
        { SeasonNumber: { } season, Number: { } number } =>
            $"S{season.ToString("D2", CultureInfo.InvariantCulture)} · E{number.ToString(CultureInfo.InvariantCulture)}",
        _ => null
    };

    /// <summary>
    /// The top of the page (<c>_TitleHero</c>, with <c>_TitleActions</c>). The
    /// one primary button marks the episode watched; a watched episode gets
    /// the green "Watched …" box instead, with "Mark as unwatched" in the "⋯"
    /// menu; one that may not be marked yet gets "Airs …". A visitor gets the
    /// sign-in card. The next episode follows as its own card.
    /// </summary>
    public TitleHeroModel BuildHero(string? returnUrl = null)
    {
        var episode = Episode!;
        var name = EpisodeName(episode);
        var seriesId = episode.SeriesId is > 0 and var sid ? sid : (int?)null;

        var meta = new List<string>();
        if (RuntimeMinutes is > 0 and var minutes)
            meta.Add(TitleSummary.Runtime(minutes));
        if (HasAired && AiredDate is { } aired)
            meta.Add($"Aired {DisplayDate.Format(aired, Today)}");

        return new TitleHeroModel
        {
            Name = name,
            ShowPoster = false,
            BackgroundUrl = Art.Url,
            Back = seriesId is { } backTo
                ? new TitleBackLink(SeriesName ?? "Back to the series", "/Series/Details", UseHistory: false, RouteId: backTo)
                : new TitleBackLink("Back", "/Search", UseHistory: true),
            Parent = seriesId is { } parentId && !string.IsNullOrWhiteSpace(SeriesName)
                ? new TitleParentLink(SeriesName, "/Series/Details", parentId)
                : null,
            Status = FinaleText is { } finale ? new TitleStatusChip(finale, Active: false) : null,
            MetaCode = MetaCode,
            Meta = meta.Count == 0 ? null : string.Join(" · ", meta),
            MetaRelease = !HasAired && Release is { } release
                ? new TitleMetaRelease("Airs", new ReleaseTimeModel(release, Today))
                : null,
            ImdbRating = ImdbRating,
            RecallRating = RecallRatingCount > 0 && RecallRatingAverage is { } average
                ? average.ToString("0.0", CultureInfo.InvariantCulture)
                : null,
            RecallRatingCount = RecallRatingCount,
            Rating = IsAuthenticated
                ? new RatingWidgetModel
                {
                    RateHandler = "RateEpisode",
                    ClearHandler = "ClearEpisodeRating",
                    RouteId = episode.Id!.Value,
                    CurrentValue = CurrentUserRating,
                    TargetNoun = "episode"
                }
                : null,
            Position = Position,
            Actions = BuildActions(returnUrl, name),
            UpNext = UpNext
        };
    }

    private TitleActionsModel BuildActions(string? returnUrl, string name)
    {
        var id = Episode!.Id!.Value;
        TitleAction? primary = null;
        TitleStatusBox? status = null;
        var menu = new List<TitleMenuItem>();

        if (IsWatchedByCurrentUser)
        {
            status = new TitleStatusBox(
                WatchedOnUtc is { } watched ? $"Watched {DisplayDate.Format(watched, Today)}" : "Watched",
                Icons.Check);
            menu.Add(new TitleMenuItem
            {
                Label = "Mark as unwatched",
                Icon = Icons.MarkUnwatched,
                Handler = "ToggleWatched",
                RouteId = id,
                Id = "markUnwatched"
            });
        }
        else if (!MayBeMarked)
        {
            // Marking goes by the air date anywhere on Earth (AirDate.MayBeMarked):
            // before that, the box says when it is released, in the viewer's time.
            status = Release is { } release
                ? new TitleStatusBox("Airs", Icons.NotAiredYet, Release: new ReleaseTimeModel(release, Today), ReleaseInLabel: true)
                : new TitleStatusBox("Not aired yet", Icons.NotAiredYet);
        }
        else
        {
            primary = new TitleAction
            {
                Label = "Mark as watched",
                Detail = RuntimeMinutes is > 0 and var minutes ? TitleSummary.Runtime(minutes) : null,
                Handler = "ToggleWatched",
                RouteId = id,
                // Named, so the "Catch up?" dialog's forms always get this episode's id.
                HiddenFields = { ["episodeId"] = id.ToString(CultureInfo.InvariantCulture) },
                Icon = Icons.Check,
                PriorCheckEpisodeId = PriorUnwatchedCount > 0 ? id : null,
                PriorUnwatchedCount = PriorUnwatchedCount > 0 ? PriorUnwatchedCount : null
            };
        }

        return new TitleActionsModel
        {
            IsAuthenticated = IsAuthenticated,
            Noun = "episode",
            ReturnUrl = returnUrl,
            Primary = primary,
            Status = status,
            Menu = menu,
            MenuLabel = $"More actions for {name}",
            Like = new LikeToggleModel
            {
                Placement = LikePlacement.ActionRow,
                Handler = "ToggleLike",
                RouteId = id,
                IsLiked = IsLikedByCurrentUser,
                TargetNoun = "episode"
            },
            ShareTitle = SeriesName is null ? name : $"{SeriesName}: {name}"
        };
    }

    private static string EpisodeName(Episode episode) =>
        string.IsNullOrWhiteSpace(episode.Name) ? "Untitled episode" : episode.Name;

    /// <summary>OMDb uses the literal string "N/A" for missing values.</summary>
    private static string? OmdbValue(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : raw.Trim();

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
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
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
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
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
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed clearing rating for episode {EpisodeId}.", id);
            this.SetErrorToast("Could not update your rating right now.");
        }

        return RedirectToPage(new { id });
    }

    /// <summary>"S01E03" for the toast; "the episode" when TheTVDB has no numbers for it.</summary>
    private static string SlateCode(Episode episode) =>
        episode is { SeasonNumber: { } season, Number: { } number } ? $"S{season:D2}E{number:D2}" : "the episode";

    /// <summary>
    /// Marks or unmarks an episode: this one (the action row, the "⋯" menu), or,
    /// with <paramref name="episodeId"/>, another of the same series (the Up
    /// next card's check, or the "Catch up?" dialog it opened). Either way the
    /// page comes back to this episode.
    /// </summary>
    public async Task<IActionResult> OnPostToggleWatchedAsync(
        [FromRoute] int id,
        CancellationToken cancellationToken,
        [FromForm] int? episodeId = null)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        var targetId = episodeId is > 0 and var other ? other : id;

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
            var toggled = await watchProgressService.ToggleEpisodeWatchedAsync(userId, seriesId, targetId, cancellationToken);
            switch (toggled.Outcome)
            {
                case EpisodeWatchOutcome.MarkedUnwatched:
                    this.SetInfoToast("Episode marked as not watched.");
                    break;
                case EpisodeWatchOutcome.MarkedWatched:
                    // Marking from here puts the series in the library too, as on the series
                    // page, and resumes a series the user had stopped watching; say so.
                    var clause = MarkLibraryEffect.Clause(toggled.AddedToLibrary, toggled.ResumedWatching);
                    this.SetWatchedToast(
                        clause is not null
                            ? $"Marked {await SlateCodeAsync(episode, targetId, cancellationToken)} as watched{clause}."
                            : "Episode marked as watched.",
                        toggled.CaughtUp, Today,
                        keepMessage: clause is not null);
                    break;
                case EpisodeWatchOutcome.NotAired:
                    this.SetErrorToast("You can't mark an episode as watched before it has aired.");
                    break;
                case EpisodeWatchOutcome.EpisodeNotInSeries:
                    this.SetErrorToast("Could not verify this episode against its series right now.");
                    break;
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Unexpected error while toggling watched status for episode {EpisodeId}.", targetId);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Marks the episode (this one, or <paramref name="episodeId"/> as in
    /// <see cref="OnPostToggleWatchedAsync"/>) AND every earlier episode in the
    /// same series (by watch order, the same kind) as watched, skipping ones
    /// already watched.
    /// </summary>
    public async Task<IActionResult> OnPostMarkWatchedThroughAsync(
        [FromRoute] int id,
        CancellationToken cancellationToken,
        [FromForm] int? episodeId = null)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        var targetId = episodeId is > 0 and var other ? other : id;

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

            var result = await watchProgressService.MarkWatchedThroughAsync(userId, seriesId, targetId, cancellationToken);

            if (!result.EpisodeFound)
            {
                logger.LogWarning(
                    "MarkWatchedThroughAsync rejected: episode {EpisodeId} is not part of series {SeriesId}.",
                    targetId, seriesId);
                this.SetErrorToast("Could not verify this episode against its series right now.");
            }
            else if (!result.HasAired)
            {
                this.SetErrorToast("You can't mark an episode as watched before it has aired.");
            }
            else
            {
                var clause = MarkLibraryEffect.Clause(result.AddedToLibrary, result.ResumedWatching);
                var marked = result.MarkedCount > 1
                    ? $"Marked {result.MarkedCount} episodes"
                    : $"Marked {(clause is null ? "" : await SlateCodeAsync(episode, targetId, cancellationToken))}";
                this.SetSuccessToastWithWatchedUndo(
                    clause is not null
                        ? $"{marked} as watched{clause}."
                        : result.MarkedCount > 1 ? $"{marked} as watched." : "Episode marked as watched.",
                    seriesId,
                    result.Batch,
                    caughtUp: result.CaughtUp,
                    today: Today,
                    resumedFromStoppedUtc: result.ResumedFromStoppedUtc);
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Unexpected error while marking episode {EpisodeId} and earlier episodes as watched.", targetId);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// "S01E03" for the toast: this episode's own numbers, or, for another
    /// episode of the series (the Up next card's), the cached aggregate's.
    /// "the episode" when neither has numbers for it.
    /// </summary>
    private async Task<string> SlateCodeAsync(Episode page, int targetId, CancellationToken cancellationToken)
    {
        if (targetId == page.Id)
            return SlateCode(page);

        var aggregate = await theTvDbService.TryGetSeriesAggregateAsync(page.SeriesId!.Value, logger, "Episode toast", cancellationToken);
        return aggregate?.Episodes.FirstOrDefault(e => e.Id == targetId) is { SeasonNumber: { } season, EpisodeNumber: { } number }
            ? $"S{season:D2}E{number:D2}"
            : "the episode";
    }

    /// <summary>
    /// What every POST handler here returns to an anonymous caller: the page is
    /// public, so <c>[Authorize]</c> can't guard its handlers.
    /// </summary>
    private IActionResult SignInRequired(int id, string toDoWhat)
    {
        this.SetErrorToast($"You need to be signed in to {toDoWhat}.");
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Cache-through OMDb lookup for one episode: serves the cached snapshot when
    /// it's still fresh, otherwise calls OMDb live and stores the result. Unlike
    /// series (enriched proactively by <c>UpdateOmdbInfoTimer</c>), episodes are
    /// only enriched on demand — there are far more of them, so eagerly fetching
    /// every one isn't worth the OMDb quota.
    /// </summary>
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
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
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
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogWarning(ex, "Could not load series {SeriesId} for the episode header.", seriesId);
            return null;
        }
    }

    /// <summary>
    /// Fills <see cref="PreviousEpisode"/> / <see cref="NextEpisode"/> from the
    /// episodes in watch order (specials last), the order everything about
    /// watching uses. Leaves them null when the current episode isn't in the
    /// list or has no neighbour on that side.
    /// </summary>
    private void SetEpisodeNav(IReadOnlyList<WatchableEpisode> ordered, int index)
    {
        if (index > 0)
            PreviousEpisode = EpisodeNavLink.For(ordered[index - 1]);

        if (index < ordered.Count - 1)
            NextEpisode = EpisodeNavLink.For(ordered[index + 1]);
    }

    /// <summary>
    /// "Episode 1 of 21 · Season 52" and, signed in, how many of the season's
    /// released regular episodes are watched; a special is "Special 3 of 4"
    /// and nothing more (specials never count toward progress).
    /// </summary>
    private void SetPosition(IReadOnlyList<WatchableEpisode> ordered, WatchableEpisode current, IReadOnlySet<int>? watched)
    {
        if (current.SeasonNumber is not { } season)
            return;

        var inSeason = ordered.Where(e => e.SeasonNumber == season).ToList();
        var place = inSeason.IndexOf(current) + 1;

        if (current.IsSpecial)
        {
            Position = new TitlePositionModel($"Special {place} of {inSeason.Count}");
            return;
        }

        var label = $"Episode {place} of {inSeason.Count} · Season {season}";
        if (watched is null)
        {
            Position = new TitlePositionModel(label, SeasonNumber: season);
            return;
        }

        var released = inSeason.Where(e => e.IsReleasedBy(Now)).ToList();
        Position = new TitlePositionModel(label, released.Count(e => watched.Contains(e.Id)), released.Count, season);
    }

    /// <summary>
    /// The next episode's card: its still by the shared rule (the aggregate's,
    /// else its own record's from the caches only, never a request, else the
    /// series' background art), when it airs, and its control for a signed-in
    /// user.
    /// </summary>
    private async Task SetUpNextAsync(
        SeriesAggregate series, WatchableEpisode next, IReadOnlySet<int>? watched, Guid? userId, CancellationToken cancellationToken)
    {
        Episode? record = null;
        if (EpisodeArt.StillInAggregate(series, next.Id) is null)
        {
            try
            {
                record = await theTvDbService.GetCachedEpisodeDetailsAsync(next.Id, cancellationToken);
            }
            catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
            {
                logger.LogWarning(ex, "Could not read episode {EpisodeId} from the cache for its still.", next.Id);
            }
        }

        var released = next.IsReleasedBy(Now);
        var mark = watched is null
            ? UpNextMark.None
            : watched.Contains(next.Id)
                ? UpNextMark.Watched
                : AirDate.MayBeMarked(next.Aired, Now) ? UpNextMark.Unwatched : UpNextMark.NotAired;

        var prior = 0;
        if (mark == UpNextMark.Unwatched && userId is { } user)
            prior = await watchProgressService.GetPriorUnwatchedCountAsync(user, series.TvdbId, next.Id, cancellationToken);

        UpNext = new UpNextModel
        {
            EpisodeId = next.Id,
            Code = next.SlateCode(),
            Title = string.IsNullOrWhiteSpace(next.Name) ? "Untitled episode" : next.Name,
            ImageUrl = EpisodeArt.Resolve(series, next.Id, record).Url,
            DateText = released
                ? next.Aired is { } aired ? $"Aired {DisplayDate.Format(aired, Today)}" : null
                : next.Release is not null ? "Airs" : null,
            Release = !released && next.Release is { } release ? new ReleaseTimeModel(release, Today) : null,
            Mark = mark,
            RouteId = Episode!.Id!.Value,
            Handler = "ToggleWatched",
            PriorUnwatchedCount = prior
        };
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

            // The page renders its own "no such episode" state (with a way back
            // to the series) rather than the generic 404 page.
            if (Episode is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            // Until the series is loaded, the episode's own record is all there is.
            Art = EpisodeArt.Resolve(series: null, id, Episode);
            RuntimeMinutes = Episode.Runtime is > 0 ? Episode.Runtime : null;
            GuestStars = CastBuilder.GuestStars(Episode.Characters);

            if (DateOnly.TryParse(Episode.Aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
                AiredDate = aired;

            var signedIn = currentUserService.TryGetUserId(out var userId);
            IReadOnlySet<int>? watched = null;

            if (signedIn)
            {
                WatchedOnUtc = await episodeWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
                IsWatchedByCurrentUser = WatchedOnUtc is not null;

                IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Episode, id, cancellationToken);
                CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Episode, id, cancellationToken);

                if (!IsWatchedByCurrentUser && Episode.SeriesId is > 0)
                    PriorUnwatchedCount = await watchProgressService.GetPriorUnwatchedCountAsync(userId, Episode.SeriesId.Value, id, cancellationToken);
            }

            if (Episode.SeriesId is > 0 && Episode.Id is { } currentId
                && await LoadSeriesHeaderAsync(Episode.SeriesId.Value, cancellationToken) is { } series)
            {
                Series = series;
                Art = EpisodeArt.Resolve(series, currentId, Episode);
                RuntimeMinutes ??= series.Episodes.FirstOrDefault(e => e.Id == currentId)?.RuntimeMinutes is > 0 and var minutes ? minutes : null;

                if (signedIn)
                    watched = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, series.TvdbId, cancellationToken);

                var ordered = WatchProgressCalculator.Order(series.ToWatchableEpisodes());
                var index = ordered.ToList().FindIndex(e => e.Id == currentId);
                if (index >= 0)
                {
                    SetEpisodeNav(ordered, index);
                    SetPosition(ordered, ordered[index], watched);
                    if (index < ordered.Count - 1)
                        await SetUpNextAsync(series, ordered[index + 1], watched, signedIn ? userId : null, cancellationToken);
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

            return Page();
        }
        // TheTVDB failing and TheTVDB not answering in time are the same to the
        // visitor: the page's 503 state. A visitor who went away is neither.
        catch (Exception ex) when (ex is TheTvDbApiException || ex.IsTimeoutFor(cancellationToken))
        {
            logger.LogWarning(ex, "TheTVDB API error or timeout while loading episode {EpisodeId}.", id);
            return Failed(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
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

/// <summary>
/// The previous or next episode, for the two cards at the foot of the page:
/// "S04E03" and its title.
/// </summary>
public sealed record EpisodeNavLink(int Id, int? SeasonNumber, int? EpisodeNumber, string? Title = null)
{
    public static EpisodeNavLink For(WatchableEpisode episode) =>
        new(episode.Id, episode.SeasonNumber, episode.EpisodeNumber, string.IsNullOrWhiteSpace(episode.Name) ? null : episode.Name);

    /// <summary>"S04E03", or null when either number is missing (the card then shows its title alone).</summary>
    public string? Code =>
        SeasonNumber is { } s && EpisodeNumber is { } e
            ? $"S{s:D2}E{e:D2}"
            : null;
}
