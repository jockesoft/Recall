using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Series;

/// <summary>
/// Public, anonymous-friendly series details page. Library/watched/like/rating
/// actions are only shown and only take effect when signed in.
/// </summary>
public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    IEpisodeWatchRepository episodeWatchRepository,
    IWatchProgressService watchProgressService,
    ILikeRepository likeRepository,
    IRatingRepository ratingRepository,
    IOmdbSnapshotStore omdbSnapshotStore,
    ILogger<DetailsModel> logger)
    : PageModel
{
    public TvSeriesDetails? Series { get; private set; }
    public SeriesAggregate? Aggregate { get; private set; }

    /// <summary>OMDb enrichment for this series, when the background job has stored it.</summary>
    public OmdbSeries? Omdb { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int? Season { get; set; }

    public bool IsAuthenticated => currentUserService.IsAuthenticated;

    public bool IsTrackedByCurrentUser { get; private set; }

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

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to like a series.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
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
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to rate a series.");
            return RedirectToPage(new { id, season = Season });
        }

        if (value is < 1 or > 10)
            return RedirectToPage(new { id, season = Season });

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
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
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to rate a series.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
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

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
            return Unauthorized();

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

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

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            switch (await watchProgressService.ToggleEpisodeWatchedAsync(userId, id, episodeId, cancellationToken))
            {
                case EpisodeWatchOutcome.MarkedUnwatched:
                    this.SetInfoToast("Episode marked as not watched.");
                    break;
                case EpisodeWatchOutcome.MarkedWatched:
                    // Make sure the series is in the users library, otherwise why track progress
                    await AddToPersonalLibraryAsync(userId, id, onlyAdd: true, cancellationToken);
                    this.SetSuccessToast("Episode marked as watched.");
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

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

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
                // Make sure the series is in the users library, otherwise why track progress
                await AddToPersonalLibraryAsync(userId, id, onlyAdd: true, cancellationToken);
                this.SetSuccessToastWithWatchedUndo(
                    result.MarkedCount > 1
                        ? $"Marked {result.MarkedCount} episodes as watched."
                        : "Episode marked as watched.",
                    id,
                    result.Batch);
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

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id, season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

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
                // Make sure the series is in the users library, otherwise why track progress
                await AddToPersonalLibraryAsync(userId, id, onlyAdd: true, cancellationToken);
                this.SetSuccessToastWithWatchedUndo(
                    result.Batch.InsertedCount == 1
                        ? "Marked 1 episode as watched."
                        : $"Marked {result.Batch.InsertedCount} episodes as watched.",
                    id,
                    result.Batch);
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

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return RedirectToPage(new { id, season });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

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
    /// un-watch the sender's own episodes.
    /// </summary>
    public async Task<IActionResult> OnPostUndoWatchedAsync(
        [FromRoute] int id,
        [FromForm] long stamp,
        [FromForm] string? returnUrl,
        CancellationToken cancellationToken)
    {
        IActionResult Back() =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? LocalRedirect(returnUrl)
                : RedirectToPage(new { id, season = Season });

        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched episodes.");
            return Back();
        }

        if (stamp <= 0 || stamp > DateTime.MaxValue.Ticks)
            return Back();

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            var removed = await watchProgressService.UndoWatchedBatchAsync(
                userId, id, new DateTime(stamp, DateTimeKind.Utc), cancellationToken);

            this.SetInfoToast(removed switch
            {
                0 => "Nothing left to undo.",
                1 => "Undone — 1 episode marked as not watched.",
                _ => $"Undone — {removed} episodes marked as not watched."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed undoing a bulk watch for series {SeriesId}.", id);
            this.SetErrorToast("Could not undo that right now.");
        }

        return Back();
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

            if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
                return Page();

            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            IsTrackedByCurrentUser = await trackedSeriesRepository.ExistsAsync(userId, id, cancellationToken);
            IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Series, id, cancellationToken);
            CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Series, id, cancellationToken);
            WatchedEpisodeIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, [id], cancellationToken);
            WatchedDatesByEpisodeId = await episodeWatchRepository.GetWatchedUtcByEpisodeAsync(userId, id, cancellationToken);

            // Reuse the aggregate already loaded above — no extra TheTVDB call.
            WatchProgress = watchProgressService.BuildProgress(id, Aggregate.ToWatchableEpisodes(), WatchedEpisodeIds);
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
        if (season == 0) return "SP";
        else return "S" + season.ToString("D2");
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