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
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Series;

/// <summary>
/// Public, anonymous-friendly series details page. Library/watched/like/rating
/// actions are only shown and only take effect when signed in.
/// </summary>
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.PublicDetailsPolicy)]
public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
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

    [BindProperty(SupportsGet = true)]
    public int? Season { get; set; }

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

    /// <summary>The series' seasons in the order the picker shows them: numbered seasons, then the specials.</summary>
    public IReadOnlyList<int> SeasonNumbers { get; private set; } = [];

    /// <summary>
    /// The season whose episodes are listed: the one asked for in the URL, or
    /// <see cref="WatchProgressCalculator.DefaultSeason"/> (for a visitor who
    /// is not signed in, that is the first season).
    /// </summary>
    public int? SelectedSeason { get; private set; }

    public IReadOnlyList<CastPerson> Cast { get; private set; } = [];

    public IReadOnlyList<CastPerson> Crew { get; private set; } = [];

    /// <summary>
    /// The top of the page (see <c>_TitleHeader.cshtml</c>). For a series in
    /// the library the one primary button is the next episode to watch, or a
    /// quiet "Up to date"; for any other series it is "Add to library". A
    /// series the user stopped watching says so, and its one button is
    /// "Resume watching" in place of the next episode.
    /// </summary>
    /// <param name="omdbGenres">
    /// OMDb's genres, shown only while the series has none of TheTVDB's (a row
    /// cached before series carried them, see <see cref="SeriesAggregate.Genres"/>).
    /// </param>
    public TitleHeaderModel BuildHeader(IReadOnlyList<string> omdbGenres, string? returnUrl = null)
    {
        var series = Aggregate!;
        var genres = series.Genres.Count > 0 ? series.Genres : omdbGenres;
        var seasonField = new Dictionary<string, string> { ["Season"] = Season?.ToString() ?? string.Empty };

        TitleAction? primary = null;
        TitleState? state = null;

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
            state = new TitleState($"You stopped watching on {DisplayDate.Format(stoppedUtc, Today)}", Icons.Stopped, Neutral: true);
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
                Handler = "ToggleEpisodeWatched",
                RouteId = series.TvdbId,
                HiddenFields =
                {
                    ["episodeId"] = next.Id.ToString(),
                    ["Season"] = next.SeasonNumber?.ToString() ?? string.Empty
                },
                Icon = Icons.Check
            };
        }
        else
        {
            // No aired regular episode is left. "Up to date" is for a series
            // the user has started; one never started has simply had nothing
            // to watch yet (announced, or only specials so far), which is also
            // why the Library keeps it under Watching (SeriesLibraryStateRule).
            state = WatchProgress switch
            {
                { HasStarted: true } => new TitleState("Up to date", Icons.Success),
                not null => new TitleState("In your library · nothing aired yet", Icons.Success),
                _ => new TitleState("In your library", Icons.Success)
            };
        }

        return new TitleHeaderModel
        {
            Name = series.Name,
            ImageUrl = series.ImageUrl,
            Genres = genres,
            Summary = TitleSummary.ForSeries(series),
            Noun = "series",
            IsAuthenticated = IsAuthenticated,
            ReturnUrl = returnUrl,
            Primary = primary,
            State = state,
            Like = new LikeToggleModel
            {
                Handler = "ToggleSeriesLike",
                RouteId = series.TvdbId,
                HiddenFields = seasonField,
                IsLiked = IsLikedByCurrentUser,
                TargetNoun = "series"
            },
            Rating = new RatingWidgetModel
            {
                RateHandler = "RateSeries",
                ClearHandler = "ClearSeriesRating",
                RouteId = series.TvdbId,
                HiddenFields = seasonField,
                CurrentValue = CurrentUserRating,
                TargetNoun = "series"
            }
        };
    }

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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your library right now.");
        }

        return RedirectToPage(new { id, season = Season });
    }

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "like a series");

        try
        {
            await likeRepository.ToggleAsync(userId, LikeTargetType.Series, id, id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage(new { id, season = Season });
    }

    public async Task<IActionResult> OnPostRateSeriesAsync(
        [FromRoute] int id,
        [FromForm] int value,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate a series");

        if (value is < 1 or > 10)
            return RedirectToPage(new { id, season = Season });

        try
        {
            await ratingRepository.RateAsync(userId, RatingTargetType.Series, id, id, value, cancellationToken);
            this.SetSuccessToast("Rating saved.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed rating series {SeriesId}.", id);
            this.SetErrorToast("Could not save your rating right now.");
        }

        return RedirectToPage(new { id, season = Season });
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed clearing rating for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your rating right now.");
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

        if (!currentUserService.TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            var priorUnwatchedCount = await watchProgressService.GetPriorUnwatchedCountAsync(userId, id, episodeId, cancellationToken);

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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking episode {EpisodeId} and earlier episodes as watched for series {SeriesId}.", episodeId, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id, season = Season });
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking season {Season} of series {SeriesId} as watched.", season, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id, season });
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking season {Season} of series {SeriesId} as not watched.", season, id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id, season });
    }

    /// <summary>
    /// Reverses one bulk "mark watched" — posted by the Undo button in the
    /// success toast (see <c>_ToastMessages.cshtml</c>), from this page or from
    /// Episodes/Details. <paramref name="stamp"/> is the batch's
    /// <c>WatchedUtc</c> in ticks; only the current user's own rows carrying
    /// exactly that timestamp are removed, so a forged value can at worst
    /// un-watch the sender's own episodes. When the mark had resumed a series
    /// the user stopped watching, <paramref name="stoppedStamp"/> is the
    /// stopped date it cleared (ticks) and the undo puts it back; a forged one
    /// can at worst stop the sender's own series.
    /// </summary>
    public async Task<IActionResult> OnPostUndoWatchedAsync(
        [FromRoute] int id,
        [FromForm] long stamp,
        [FromForm] string? returnUrl,
        CancellationToken cancellationToken,
        [FromForm] long? stoppedStamp = null)
    {
        IActionResult Back() =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? LocalRedirect(returnUrl)
                : RedirectToPage(new { id, season = Season });

        if (!currentUserService.TryGetUserId(out var userId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return Back();
        }

        if (stamp <= 0 || stamp > DateTime.MaxValue.Ticks)
            return Back();

        try
        {
            var removed = await watchProgressService.UndoWatchedBatchAsync(
                userId, id, new DateTime(stamp, DateTimeKind.Utc), cancellationToken);

            // Undoing the mark that resumed a stopped series stops it again, as it
            // was. Only then: an ordinary unmark never touches the stopped state.
            var stoppedAgain = removed > 0
                               && stoppedStamp is > 0 and var ticks && ticks <= DateTime.MaxValue.Ticks
                               && await watchProgressService.RestoreStoppedAsync(
                                   userId, id, new DateTime(ticks, DateTimeKind.Utc), cancellationToken);

            var undone = removed switch
            {
                0 => "Nothing left to undo.",
                1 => "Undone — 1 episode marked as not watched.",
                _ => $"Undone — {removed} episodes marked as not watched."
            };

            this.SetInfoToast(stoppedAgain ? $"{undone} You've stopped watching the series again." : undone);
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed stopping series {SeriesId}.", id);
            this.SetErrorToast("Could not update your library right now.");
        }

        return RedirectToPage(new { id, season = Season });
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
                : RedirectToPage(new { id, season = Season });

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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resuming series {SeriesId}.", id);
            this.SetErrorToast(undo ? "Could not undo that right now." : "Could not update your library right now.");
        }

        return Back();
    }

    /// <summary>
    /// What every POST handler here returns to an anonymous caller: the page is
    /// public, so <c>[Authorize]</c> can't guard its handlers.
    /// </summary>
    private IActionResult SignInRequired(int id, string toDoWhat)
    {
        this.SetErrorToast($"You need to be signed in to {toDoWhat}.");
        return RedirectToPage(new { id, season = Season });
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

            SeasonNumbers = Aggregate.Seasons
                .Select(s => s.Number)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .Distinct()
                .OrderBy(n => EpisodeOrderingExtensions.SeasonRank(n))
                .ThenBy(n => n)
                .ToList();

            if (!currentUserService.TryGetUserId(out var userId))
            {
                // Nothing is watched for a visitor, so the default is the first season.
                SelectSeason(watchProgressService.BuildProgress(id, Aggregate.ToWatchableEpisodes(), new HashSet<int>()));
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
            SelectSeason(WatchProgress);
            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading details for id {SeriesId}.", id);
            return LoadFailed(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
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

    private void SelectSeason(SeriesWatchProgress progress)
    {
        var fallback = WatchProgressCalculator.DefaultSeason(progress)
                       ?? (SeasonNumbers.Count > 0 ? SeasonNumbers[0] : (int?)null);

        SelectedSeason = Season is { } asked && SeasonNumbers.Contains(asked)
            ? asked
            : fallback is { } season && SeasonNumbers.Contains(season)
                ? season
                : SeasonNumbers.Count > 0 ? SeasonNumbers[0] : Season;
    }

    /// <summary>Short label for a season chip: "S01", or "Specials" for season 0.</summary>
    public string GetSeasonName(int season) => season == 0 ? "Specials" : "S" + season.ToString("D2");

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
                var series = await theTvDbService.GetSeriesByIdAsync(seriesId, cancellationToken);
                if (series is null) return;

                var tracked = TrackedSeriesMappings.FromTvDbDetails(userId, series);

                if (await trackedSeriesRepository.AddAsync(tracked, cancellationToken))
                    this.SetSuccessToast("Series saved to your library.");
                else
                    this.SetInfoToast("Series is already in your library.");
            }
            else
            {
                await trackedSeriesRepository.RemoveAsync(userId, existing.Id, cancellationToken);
                this.SetInfoToast("Series removed from your library.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", seriesId);
            this.SetErrorToast("Could not update your library right now.");
        }   
    }
}