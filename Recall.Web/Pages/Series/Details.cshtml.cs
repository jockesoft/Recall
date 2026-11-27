using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;
using Recall.Web.Pages.Shared;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Stats;
using Recall.Web.Services.WatchTracking;
using System.Globalization;

namespace Recall.Web.Pages.Series;

/// <summary>
/// Public, anonymous-friendly series details page. Library/watched/like/rating
/// actions are only shown and only take effect when signed in.
/// </summary>
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.PublicDetailsPolicy)]
public sealed partial class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    ISeriesLibraryService seriesLibraryService,
    IEpisodeWatchRepository episodeWatchRepository,
    IWatchProgressService watchProgressService,
    ILikeRepository likeRepository,
    IRatingRepository ratingRepository,
    IOmdbSnapshotStore omdbSnapshotStore,
    TimeProvider timeProvider,
    ILogger<DetailsModel> logger)
    : PageModel
{
    /// <summary>Today's date in UTC, for the view's "has this episode aired" checks.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    /// <summary>Now in UTC, for "released" (EpisodeRelease) and "may be marked" (AirDate.MayBeMarked).</summary>
    public DateTime Now => AirDate.Now(timeProvider);

    public TvSeriesDetails? Series { get; private set; }
    public SeriesAggregate? Aggregate { get; private set; }

    /// <summary>OMDb enrichment for this series, when the background job has stored it.</summary>
    public OmdbSeries? Omdb { get; private set; }

    /// <summary>
    /// A season to open besides the default one: where a mark made in the
    /// episode list sends the user back to.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public int? Season { get; set; }

    /// <summary>
    /// Posted by the episode list's forms ("season-3", "episode-123"): the
    /// redirect after the action ends at that card or row, not at the top of
    /// the page. Anything else is ignored.
    /// </summary>
    [BindProperty]
    public string? Anchor { get; set; }

    public bool IsAuthenticated => currentUserService.IsAuthenticated;

    public bool IsTrackedByCurrentUser { get; private set; }

    /// <summary>
    /// When the current user stopped watching this series; null while they are
    /// watching it (or it is not in their library).
    /// </summary>
    public DateTime? StoppedUtc { get; private set; }

    /// <summary>
    /// Whether "Stop watching" is offered (<see cref="SeriesLibraryStateRule.CanStop"/>):
    /// for a series in the library that is being watched or up to date. Not
    /// for a finished one, and not for one already stopped.
    /// </summary>
    public bool CanStopWatching { get; private set; }

    /// <summary>Whether the current user has hearted this series.</summary>
    public bool IsLikedByCurrentUser { get; private set; }

    /// <summary>The current user's 1-10 rating of this series, or null when unrated.</summary>
    public int? CurrentUserRating { get; private set; }

    /// <summary>Average of every Recall user's rating of this series, when at least one exists.</summary>
    public double? RecallRatingAverage { get; private set; }

    /// <summary>How many Recall users have rated this series.</summary>
    public int RecallRatingCount { get; private set; }

    public IReadOnlySet<int> WatchedEpisodeIds { get; private set; } = new HashSet<int>();

    /// <summary>
    /// When the current user marked each watched episode, keyed by episode TVDB
    /// id. Used to show a "Watched on …" line under watched rows.
    /// </summary>
    public IReadOnlyDictionary<int, DateTime> WatchedDatesByEpisodeId { get; private set; }
        = new Dictionary<int, DateTime>();

    /// <summary>
    /// Where the signed-in user is in this series (next episode to watch / up to
    /// date). Null when not signed in.
    /// </summary>
    public SeriesWatchProgress? WatchProgress { get; private set; }

    /// <summary>
    /// The seasons as cards, in page order: numbered seasons, then the
    /// specials (see <see cref="SeasonCards"/>). Earlier seasons are flagged
    /// for the "Show N earlier seasons" group.
    /// </summary>
    public IReadOnlyList<SeasonCard> Seasons { get; private set; } = [];

    /// <summary>The cards behind "Show N earlier seasons".</summary>
    public int EarlierSeasonCount => Seasons.Count(s => s.IsEarlier);

    /// <summary>
    /// The next episode to watch, for "Jump to next": its row is in an open
    /// card. Null for a visitor and when nothing released is left.
    /// </summary>
    public int? NextEpisodeId => WatchProgress?.NextUnwatchedEpisode?.Id;

    /// <summary>
    /// The episode the hero's primary button marks ("Mark S05E07 watched"):
    /// the next episode of a series in the library that the user has not
    /// stopped. Its row's control is the amber "ready" one; null otherwise
    /// (not in the library, stopped, up to date, a visitor).
    /// </summary>
    public int? ReadyEpisodeId => IsTrackedByCurrentUser && StoppedUtc is null ? NextEpisodeId : null;

    /// <summary>"17 seasons · 343 episodes · 470 h": released regular episodes and how long they take.</summary>
    public string? EpisodesSummary { get; private set; }

    public IReadOnlyList<CastPerson> Cast { get; private set; } = [];

    public IReadOnlyList<CastPerson> Crew { get; private set; } = [];

    /// <summary>IMDb's rating from the OMDb snapshot ("7.3"); null when unknown.</summary>
    public string? ImdbRating => OmdbValue(Omdb?.ImdbRating);

    /// <summary>IMDb's vote count from the OMDb snapshot ("1,234,567").</summary>
    public string? ImdbVotes => VoteCount.Format(Omdb?.ImdbVotes);

    /// <summary>OMDb's awards line, when it has one.</summary>
    public string? OmdbAwards => OmdbValue(Omdb?.Awards);

    /// <summary>The network for the Overview's facts.</summary>
    public string? Network => Aggregate?.Network;

    /// <summary>
    /// The next regular episode not released yet (the soonest by release
    /// moment), for the Overview's "Next episode" and the up-to-date box.
    /// </summary>
    public (WatchableEpisode Episode, ReleaseMoment Release)? NextRelease { get; private set; }

    /// <summary>
    /// The top of the page (<c>_TitleHero</c>, with <c>_TitleActions</c>). For
    /// a series in the library the one primary button is the next episode to
    /// watch, or a quiet "Up to date" box; for any other series it is "Add to
    /// library". A series the user stopped watching says so, and its button
    /// is "Resume watching". Stop watching and Remove from library are in the
    /// "⋯" menu.
    /// </summary>
    public TitleHeroModel BuildHero(string? returnUrl = null)
    {
        var series = Aggregate!;
        var genres = series.Genres.Count > 0 ? series.Genres : OmdbGenres();
        var seasonField = new Dictionary<string, string> { ["Season"] = Season?.ToString() ?? string.Empty };

        return new TitleHeroModel
        {
            Name = series.Name,
            PosterUrl = series.ImageUrl,
            BackgroundUrl = series.BackgroundUrl,
            Back = IsTrackedByCurrentUser
                ? new TitleBackLink("Library", "/Library", UseHistory: false)
                : new TitleBackLink("Back", "/Search", UseHistory: true),
            Genres = genres,
            Status = string.IsNullOrWhiteSpace(series.Status?.Name)
                ? null
                : new TitleStatusChip(series.Status.Name, Active: series.Status.KeepUpdated == true),
            Meta = TitleSummary.ForSeries(series),
            ImdbRating = ImdbRating,
            RecallRating = RecallRatingCount > 0 && RecallRatingAverage is { } average
                ? average.ToString("0.0", CultureInfo.InvariantCulture)
                : null,
            RecallRatingCount = RecallRatingCount,
            Rating = IsAuthenticated
                ? new RatingWidgetModel
                {
                    RateHandler = "RateSeries",
                    ClearHandler = "ClearSeriesRating",
                    RouteId = series.TvdbId,
                    HiddenFields = seasonField,
                    CurrentValue = CurrentUserRating,
                    TargetNoun = "series"
                }
                : null,
            Progress = BuildProgress(),
            Actions = BuildActions(returnUrl, seasonField)
        };
    }

    private TitleActionsModel BuildActions(string? returnUrl, Dictionary<string, string> seasonField)
    {
        var series = Aggregate!;
        TitleNotice? notice = null;
        TitleAction? primary = null;
        TitleStatusBox? status = null;

        if (!IsTrackedByCurrentUser)
        {
            primary = new TitleAction
            {
                Label = "Add to library",
                Handler = "ToggleLibrary",
                RouteId = series.TvdbId,
                HiddenFields = seasonField,
                Icon = Icons.Add
            };
        }
        else if (StoppedUtc is { } stoppedUtc)
        {
            notice = new TitleNotice($"You stopped watching on {DisplayDate.Format(stoppedUtc, Today)}", Icons.Stopped);
            primary = new TitleAction
            {
                Label = "Resume watching",
                Handler = "ResumeWatching",
                RouteId = series.TvdbId,
                HiddenFields = seasonField,
                Icon = Icons.Resume
            };
        }
        else if (WatchProgress is { HasEpisodes: true, NextUnwatchedEpisode: { } next })
        {
            primary = new TitleAction
            {
                Label = $"Mark {next.SlateCode()} watched",
                Detail = NextEpisodeDetail(next),
                Handler = "ToggleEpisodeWatched",
                RouteId = series.TvdbId,
                HiddenFields =
                {
                    ["episodeId"] = next.Id.ToString(CultureInfo.InvariantCulture),
                    ["Season"] = next.SeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                },
                Icon = Icons.Check
            };
        }
        else
        {
            // No released regular episode is left. "Up to date" is for a series
            // the user has started; one never started has simply had nothing
            // to watch yet (announced, or only specials so far), which is also
            // why the Library keeps it under Watching (SeriesLibraryStateRule).
            var (detail, release) = NextRelease is { } upcoming
                ? ($"{upcoming.Episode.SlateCode()} airs", new ReleaseTimeModel(upcoming.Release, Today))
                : (series.Status?.KeepUpdated == false ? "The series has ended" : "Nothing announced yet", (ReleaseTimeModel?)null);

            status = WatchProgress is { HasStarted: true }
                ? new TitleStatusBox("Up to date", Icons.UpToDate, detail, release)
                : new TitleStatusBox("In your library · nothing aired yet", Icons.NotAiredYet, detail, release);
        }

        var menu = new List<TitleMenuItem>();
        if (CanStopWatching)
        {
            menu.Add(new TitleMenuItem
            {
                Label = "Stop watching",
                Icon = Icons.Stopped,
                Handler = "StopWatching",
                RouteId = series.TvdbId,
                HiddenFields = seasonField,
                Id = "stopWatching"
            });
        }

        if (IsTrackedByCurrentUser)
        {
            menu.Add(new TitleMenuItem
            {
                Label = "Remove from library",
                Icon = Icons.Delete,
                ModalId = RemoveFromLibraryModalId,
                Id = "removeFromLibrary"
            });
        }

        return new TitleActionsModel
        {
            IsAuthenticated = IsAuthenticated,
            Noun = "series",
            ReturnUrl = returnUrl,
            Notice = notice,
            Primary = primary,
            Status = status,
            Menu = menu,
            MenuLabel = $"More actions for {series.Name}",
            Like = new LikeToggleModel
            {
                Placement = LikePlacement.ActionRow,
                Handler = "ToggleSeriesLike",
                RouteId = series.TvdbId,
                HiddenFields = seasonField,
                IsLiked = IsLikedByCurrentUser,
                TargetNoun = "series"
            },
            ShareTitle = series.Name
        };
    }

    /// <summary>The confirmation behind "Remove from library" in the "⋯" menu.</summary>
    public const string RemoveFromLibraryModalId = "removeFromLibraryModal";

    /// <summary>"Aired Fri, Nov 20 · 82 min": the episode's runtime, else the series' average.</summary>
    private string? NextEpisodeDetail(WatchableEpisode next)
    {
        var parts = new List<string>();
        if (next.Aired is { } aired)
            parts.Add($"Aired {DisplayDate.Format(aired, Today)}");
        if (SeasonCards.Minutes(Aggregate!, [next.Id]) is { } minutes)
            parts.Add($"{minutes.ToString(CultureInfo.InvariantCulture)} min");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// The hero's progress: only for a series in the library with something
    /// released. Remaining time is the unwatched released regular episodes'
    /// runtimes, each falling back to the series' average.
    /// </summary>
    private TitleProgressModel? BuildProgress()
    {
        if (!IsTrackedByCurrentUser || WatchProgress is not { ReleasedCount: > 0 } progress)
            return null;

        var unwatched = progress.OrderedEpisodes
            .Where(e => !e.IsSpecial && e.IsReleasedBy(Now) && !progress.WatchedEpisodeIds.Contains(e.Id))
            .Select(e => e.Id);

        return new TitleProgressModel(
            progress.WatchedReleasedCount,
            progress.ReleasedCount,
            SeasonCards.Minutes(Aggregate!, unwatched),
            WatchedDatesByEpisodeId.Count > 0 ? DisplayDate.Format(WatchedDatesByEpisodeId.Values.Max(), Today) : null);
    }

    private IReadOnlyList<string> OmdbGenres() =>
        (OmdbValue(Omdb?.Genre) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>OMDb uses the literal string "N/A" for missing values.</summary>
    private static string? OmdbValue(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : raw.Trim();

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
        => await LoadPageAsync(id, cancellationToken);

    public async Task<IActionResult> OnPostToggleLibraryAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "manage your library");

        try
        {
            await ToggleLibraryAsync(userId, id, cancellationToken);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your library right now.");
        }

        return BackToPage(id, Season);
    }

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "like a series");

        try
        {
            await likeRepository.ToggleAsync(userId, LikeTargetType.Series, id, id, cancellationToken);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed toggling like for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return BackToPage(id, Season);
    }

    public async Task<IActionResult> OnPostRateSeriesAsync(
        [FromRoute] int id,
        [FromForm] int value,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate a series");

        if (value is < 1 or > 10)
            return BackToPage(id, Season);

        try
        {
            await ratingRepository.RateAsync(userId, RatingTargetType.Series, id, id, value, cancellationToken);
            this.SetSuccessToast("Rating saved.");
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed rating series {SeriesId}.", id);
            this.SetErrorToast("Could not save your rating right now.");
        }

        return BackToPage(id, Season);
    }

    public async Task<IActionResult> OnPostClearSeriesRatingAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate a series");

        try
        {
            await ratingRepository.RemoveRatingAsync(userId, RatingTargetType.Series, id, cancellationToken);
            this.SetInfoToast("Rating removed.");
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed clearing rating for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your rating right now.");
        }

        return BackToPage(id, Season);
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

        if (!currentUserService.TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            var priorUnwatchedCount = await watchProgressService.GetPriorUnwatchedCountAsync(userId, id, episodeId, cancellationToken);

            return new JsonResult(new { priorUnwatchedCount });
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
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
            return BackToPage(id, Season);

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        try
        {
            var toggled = await watchProgressService.ToggleEpisodeWatchedAsync(userId, id, episodeId, cancellationToken);
            switch (toggled.Outcome)
            {
                case EpisodeWatchOutcome.MarkedUnwatched:
                    this.SetInfoToast("Episode marked as not watched.");
                    break;
                case EpisodeWatchOutcome.MarkedWatched:
                    // The service put the series in the library if it wasn't there,
                    // and resumed it if the user had stopped watching it; say so.
                    this.SetWatchedToast(
                        WatchedMessage(1, toggled.AddedToLibrary, toggled.ResumedWatching),
                        toggled.CaughtUp, Today,
                        keepMessage: toggled.AddedToLibrary is not null || toggled.ResumedWatching is not null);
                    break;
                case EpisodeWatchOutcome.NotAired:
                    this.SetErrorToast("You can't mark an episode as watched before it has aired.");
                    break;
                case EpisodeWatchOutcome.EpisodeNotInSeries:
                    this.SetErrorToast("That episode doesn't belong to this series.");
                    break;
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed toggling watched state for series {SeriesId}, episode {EpisodeId}.", id, episodeId);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return BackToPage(id, Season);
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
            return BackToPage(id, Season);

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        try
        {
            var result = await watchProgressService.MarkWatchedThroughAsync(userId, id, episodeId, cancellationToken);

            if (!result.EpisodeFound)
            {
                logger.LogWarning(
                    "MarkWatchedThroughAsync rejected: episode {EpisodeId} is not part of series {SeriesId}.",
                    episodeId, id);
                this.SetErrorToast("That episode doesn't belong to this series.");
            }
            else if (!result.HasAired)
            {
                this.SetErrorToast("You can't mark an episode as watched before it has aired.");
            }
            else
            {
                this.SetSuccessToastWithWatchedUndo(
                    WatchedMessage(result.MarkedCount, result.AddedToLibrary, result.ResumedWatching),
                    id,
                    result.Batch,
                    caughtUp: result.CaughtUp,
                    today: Today,
                    resumedFromStoppedUtc: result.ResumedFromStoppedUtc);
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed marking episode {EpisodeId} and earlier episodes as watched for series {SeriesId}.", episodeId, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return BackToPage(id, Season);
    }

    /// <summary>Marks every aired episode of the selected season watched.</summary>
    public async Task<IActionResult> OnPostMarkSeasonWatchedAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (Season is not { } season)
            return RedirectToPage(new { id });

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        try
        {
            var result = await watchProgressService.MarkSeasonWatchedAsync(userId, id, season, cancellationToken);

            if (!result.SeasonFound)
            {
                this.SetErrorToast("That season doesn't belong to this series.");
            }
            else if (result.Batch.InsertedCount == 0)
            {
                this.SetInfoToast("Nothing to mark — every aired episode in this season is already watched.");
            }
            else
            {
                this.SetSuccessToastWithWatchedUndo(
                    WatchedMessage(result.Batch.InsertedCount, result.AddedToLibrary, result.ResumedWatching, one: "Marked 1 episode as watched."),
                    id,
                    result.Batch,
                    caughtUp: result.CaughtUp,
                    today: Today,
                    resumedFromStoppedUtc: result.ResumedFromStoppedUtc);
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed marking season {Season} of series {SeriesId} as watched.", season, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return BackToPage(id, season);
    }

    /// <summary>Removes the watched mark from every episode of the selected season.</summary>
    public async Task<IActionResult> OnPostMarkSeasonUnwatchedAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (Season is not { } season)
            return RedirectToPage(new { id });

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched episodes");

        try
        {
            var removed = await watchProgressService.MarkSeasonUnwatchedAsync(userId, id, season, cancellationToken);

            this.SetInfoToast(removed == 1
                ? "Marked 1 episode as not watched."
                : $"Marked {removed} episodes as not watched.");
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed marking season {Season} of series {SeriesId} as not watched.", season, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return BackToPage(id, season);
    }

    /// <summary>What an Undo says when there is nothing (left) to remove.</summary>
    public const string NothingToUndo = "Nothing left to undo.";

    /// <summary>
    /// Reverses one bulk "mark watched" — posted by the Undo button in the
    /// success toast (see <c>_ToastMessages.cshtml</c>), from this page or from
    /// Episodes/Details or the Dashboard. <paramref name="batch"/> is the
    /// batch's id; only the current user's own rows in this series carrying it
    /// are removed, so a forged value can at worst un-watch the sender's own
    /// episodes. A form without one (a toast rendered before 2026-11-13, whose
    /// form posted the batch's timestamp as <c>stamp</c>) gets "Nothing left to
    /// undo." and changes nothing. When the mark had resumed a series
    /// the user stopped watching, <paramref name="stoppedStamp"/> is the
    /// stopped date it cleared (ticks) and the undo puts it back; a forged one
    /// can at worst stop the sender's own series.
    /// </summary>
    public async Task<IActionResult> OnPostUndoWatchedAsync(
        [FromRoute] int id,
        [FromForm] Guid? batch,
        [FromForm] string? returnUrl,
        CancellationToken cancellationToken,
        [FromForm] long? stoppedStamp = null)
    {
        IActionResult Back() =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? LocalRedirect(returnUrl)
                : BackToPage(id, Season);

        if (!currentUserService.TryGetUserId(out var userId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return Back();
        }

        if (batch is not { } batchId || batchId == Guid.Empty)
        {
            this.SetInfoToast(NothingToUndo);
            return Back();
        }

        try
        {
            var removed = await watchProgressService.UndoWatchedBatchAsync(userId, id, batchId, cancellationToken);

            // Undoing the mark that resumed a stopped series stops it again, as it
            // was. Only then: an ordinary unmark never touches the stopped state.
            var stoppedAgain = removed > 0
                               && stoppedStamp is > 0 and var ticks && ticks <= DateTime.MaxValue.Ticks
                               && await watchProgressService.RestoreStoppedAsync(
                                   userId, id, new DateTime(ticks, DateTimeKind.Utc), cancellationToken);

            var undone = removed switch
            {
                0 => NothingToUndo,
                1 => "Undone — 1 episode marked as not watched.",
                _ => $"Undone — {removed} episodes marked as not watched."
            };

            this.SetInfoToast(stoppedAgain ? $"{undone} You've stopped watching the series again." : undone);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed undoing a bulk watch for series {SeriesId}.", id);
            this.SetErrorToast("Could not undo that right now.");
        }

        return Back();
    }

    /// <summary>
    /// "Stop watching": the series stays in the library with its history, and
    /// leaves everything that says there is something to watch. No
    /// confirmation; the toast carries an Undo (a POST to
    /// <see cref="OnPostResumeWatchingAsync"/>).
    /// </summary>
    public async Task<IActionResult> OnPostStopWatchingAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "manage your library");

        try
        {
            this.SetStopWatchingToast(await watchProgressService.StopWatchingAsync(userId, id, cancellationToken), id);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed stopping series {SeriesId}.", id);
            this.SetErrorToast("Could not update your library right now.");
        }

        return BackToPage(id, Season);
    }

    /// <summary>
    /// "Resume watching" in the header of a stopped series, and the Undo in the
    /// "Stopped watching …" toast (<paramref name="undo"/>, posted from this
    /// page or from the Dashboard, which is where <paramref name="returnUrl"/>
    /// leads back to).
    /// </summary>
    public async Task<IActionResult> OnPostResumeWatchingAsync(
        [FromRoute] int id,
        [FromForm] bool undo,
        [FromForm] string? returnUrl,
        CancellationToken cancellationToken)
    {
        IActionResult Back() =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? LocalRedirect(returnUrl)
                : BackToPage(id, Season);

        if (!currentUserService.TryGetUserId(out var userId))
        {
            this.SetErrorToast("You need to be signed in to manage your library.");
            return Back();
        }

        try
        {
            var resumed = await watchProgressService.ResumeWatchingAsync(userId, id, cancellationToken);

            if (resumed is null)
                this.SetInfoToast(undo ? "Nothing left to undo." : "You are already watching this series.");
            else if (undo)
                this.SetInfoToast($"Undone — you're still watching {resumed}.");
            else
                this.SetSuccessToast($"Resumed watching {resumed}.");
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed resuming series {SeriesId}.", id);
            this.SetErrorToast(undo ? "Could not undo that right now." : "Could not update your library right now.");
        }

        return Back();
    }

    /// <summary>
    /// Back to this page after a POST, with the season to open and, when the
    /// form came from the episode list, the card or row to scroll to.
    /// </summary>
    private RedirectToPageResult BackToPage(int id, int? season) =>
        RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id, season },
            fragment: Anchor is { } anchor && AnchorPattern().IsMatch(anchor) ? anchor : null);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(season|episode)-\d{1,10}$")]
    private static partial System.Text.RegularExpressions.Regex AnchorPattern();

    /// <summary>
    /// What every POST handler here returns to an anonymous caller: the page is
    /// public, so <c>[Authorize]</c> can't guard its handlers.
    /// </summary>
    private IActionResult SignInRequired(int id, string toDoWhat)
    {
        this.SetErrorToast($"You need to be signed in to {toDoWhat}.");
        return BackToPage(id, Season);
    }

    private async Task<IActionResult> LoadPageAsync(int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(id, cancellationToken);
            if (Aggregate is null) return NotFound();

            foreach(var remoteInfo in Aggregate.RemoteIds ?? [])
            {
                logger.LogInformation("Loading series with remote site {SourceName} with remote site ID:{RemoteId}.", remoteInfo.SourceName, remoteInfo.Id);
            }

            if (Aggregate.RemoteIds != null)
                Series = new TvSeriesDetails(
                    Aggregate.TvdbId,
                    Aggregate.Name,
                    Aggregate.Slug,
                    Aggregate.Overview,
                    Aggregate.ImageUrl,
                    Aggregate.FirstAired?.ToString("yyyy-MM-dd"),
                    Aggregate.Score,
                    Aggregate.Status != null ? Aggregate.Status.Name : "",
                    Aggregate.RemoteIds
                        .Where(r => r.SourceName?.ToLowerInvariant() == "imdb" && !string.IsNullOrWhiteSpace(r.Id))
                        .Select(r => r.Id).FirstOrDefault());

            // OMDb enrichment (genre, awards, …) — local snapshot only, populated
            // by UpdateOmdbInfoTimer. Absent until the job has run for this series.
            Omdb = await omdbSnapshotStore.GetAsync(id, cancellationToken);

            var ratingSummary = await ratingRepository.GetSummaryAsync(RatingTargetType.Series, id, cancellationToken);
            RecallRatingAverage = ratingSummary.Average;
            RecallRatingCount = ratingSummary.Count;

            (Cast, Crew) = CastBuilder.Build(Aggregate.Characters);

            if (!currentUserService.TryGetUserId(out var userId))
            {
                // Nothing is watched for a visitor, so the first season opens.
                BuildEpisodes(watchProgressService.BuildProgress(id, Aggregate.ToWatchableEpisodes(), new HashSet<int>()));
                return Page();
            }

            var tracked = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(userId, id, cancellationToken);
            IsTrackedByCurrentUser = tracked is not null;
            StoppedUtc = tracked?.StoppedUtc;
            IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Series, id, cancellationToken);
            CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Series, id, cancellationToken);
            WatchedEpisodeIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, [id], cancellationToken);
            WatchedDatesByEpisodeId = await episodeWatchRepository.GetWatchedUtcByEpisodeAsync(userId, id, cancellationToken);

            // Reuse the aggregate already loaded above — no extra TheTVDB call.
            WatchProgress = watchProgressService.BuildProgress(id, Aggregate.ToWatchableEpisodes(), WatchedEpisodeIds);
            CanStopWatching = tracked is not null
                              && SeriesLibraryStateRule.CanStop(
                                  SeriesLibraryStateRule.Of(Aggregate, WatchProgress, SeriesLibraryStateRule.IsStopped(tracked)));
            BuildEpisodes(WatchProgress);
            return Page();
        }
        // TheTVDB failing and TheTVDB not answering in time are the same to the
        // visitor: the page's 503 state. A visitor who went away is neither.
        catch (Exception ex) when (ex is TheTvDbApiException || ex.IsTimeoutFor(cancellationToken))
        {
            logger.LogWarning(ex, "TheTVDB API error or timeout while loading details for id {SeriesId}.", id);
            return LoadFailed(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Unexpected error while loading details for id {SeriesId}.", id);
            return LoadFailed(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// The page renders its own "couldn't load" state (the view checks for a
    /// null <see cref="Series"/>), with a status code that says so.
    /// </summary>
    private IActionResult LoadFailed(int statusCode)
    {
        Series = null;
        Aggregate = null;
        Response.StatusCode = statusCode;
        return Page();
    }

    /// <summary>The season cards, the Episodes heading's figures and the next release.</summary>
    private void BuildEpisodes(SeriesWatchProgress progress)
    {
        var series = Aggregate!;
        Seasons = SeasonCards.Build(series, progress, Season, Now, Today, ReadyEpisodeId);

        var released = progress.OrderedEpisodes.Where(e => !e.IsSpecial && e.IsReleasedBy(Now)).Select(e => e.Id).ToList();
        var seasons = Seasons.Count(s => !s.IsSpecials);
        var parts = new List<string>();
        if (seasons > 0)
            parts.Add(StatsFormat.Count(seasons, "season"));
        parts.Add(StatsFormat.Count(released.Count, "episode"));
        if (released.Count > 0 && SeasonCards.Minutes(series, released) is { } minutes)
            parts.Add(StatsFormat.Duration(minutes));
        EpisodesSummary = string.Join(" · ", parts);

        NextRelease = progress.OrderedEpisodes
            .Where(e => !e.IsSpecial && e.Release is not null && !e.IsReleasedBy(Now))
            .OrderBy(e => e.Release!.Utc)
            .Select(e => ((WatchableEpisode, ReleaseMoment)?)(e, e.Release!))
            .FirstOrDefault();
    }

    /// <summary>Spoken-length name: "Season 1", or "Specials".</summary>
    public string GetSeasonTitle(int season) => season == 0 ? "Specials" : $"Season {season}";

    /// <summary>
    /// "Marked 8 episodes as watched." or, when the mark also put the series in
    /// the library (the watch service does that), "Marked 8 episodes as watched
    /// and added Silo to your library."
    /// </summary>
    /// <remarks>
    /// The same goes for a series the user had stopped watching, which a mark
    /// resumes: "Marked the episode as watched and resumed watching Silo."
    /// </remarks>
    private static string WatchedMessage(
        int count, string? addedToLibrary, string? resumedWatching, string one = "Episode marked as watched.")
    {
        if (MarkLibraryEffect.Clause(addedToLibrary, resumedWatching) is not { } clause)
            return count > 1 ? $"Marked {count} episodes as watched." : one;

        return count > 1
            ? $"Marked {count} episodes as watched{clause}."
            : $"Marked the episode as watched{clause}.";
    }

    /// <summary>
    /// The "Add to library" / "Remove from library" action. (A mark adds the
    /// series by itself, in the watch service; it never removes it.)
    /// </summary>
    private async Task ToggleLibraryAsync(
        Guid userId,
        int seriesId,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(userId, seriesId, cancellationToken);

            if (existing is null)
            {
                switch ((await seriesLibraryService.AddAsync(userId, seriesId, cancellationToken)).Outcome)
                {
                    case AddSeriesOutcome.Added:
                        this.SetSuccessToast("Series saved to your library.");
                        break;
                    case AddSeriesOutcome.AlreadyInLibrary:
                        this.SetInfoToast("Series is already in your library.");
                        break;
                }
            }
            else
            {
                await trackedSeriesRepository.RemoveAsync(userId, existing.Id, cancellationToken);
                this.SetInfoToast("Series removed from your library.");
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", seriesId);
            this.SetErrorToast("Could not update your library right now.");
        }   
    }
}