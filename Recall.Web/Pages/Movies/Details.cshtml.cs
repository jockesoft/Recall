using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Movies;

/// <summary>
/// Public, anonymous-friendly movie details page. Like/watched/rating actions
/// are only shown when signed in; no library/tracking yet, that needs its own
/// data model.
/// </summary>
public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ILikeRepository likeRepository,
    IMovieWatchRepository movieWatchRepository,
    IRatingRepository ratingRepository,
    IMovieOmdbSnapshotStore omdbSnapshotStore,
    ILogger<DetailsModel> logger)
    : PageModel
{
    public MovieAggregate? Movie { get; private set; }

    /// <summary>OMDb enrichment for this movie, when the background job has stored it.</summary>
    public OmdbSeries? Omdb { get; private set; }

    public bool IsAuthenticated => currentUserService.IsAuthenticated;

    /// <summary>Whether the current user has hearted this movie.</summary>
    public bool IsLikedByCurrentUser { get; private set; }

    /// <summary>When the current user marked this movie watched, or null if they haven't.</summary>
    public DateTime? WatchedOnUtc { get; private set; }

    public bool IsWatchedByCurrentUser => WatchedOnUtc is not null;

    /// <summary>The current user's 1-10 rating of this movie, or null when unrated.</summary>
    public int? CurrentUserRating { get; private set; }

    /// <summary>Average of every Recall user's rating of this movie, when at least one exists.</summary>
    public double? RecallRatingAverage { get; private set; }

    /// <summary>How many Recall users have rated this movie.</summary>
    public int RecallRatingCount { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Movie = await theTvDbService.GetMovieAggregateByIdAsync(id, cancellationToken);
            if (Movie is null) return NotFound();

            // OMDb enrichment (rating, awards, …) — local snapshot only, populated
            // by UpdateMovieOmdbInfoTimer. Absent until the job has run for this movie.
            Omdb = await omdbSnapshotStore.GetAsync(id, cancellationToken);

            var ratingSummary = await ratingRepository.GetSummaryAsync(RatingTargetType.Movie, id, cancellationToken);
            RecallRatingAverage = ratingSummary.Average;
            RecallRatingCount = ratingSummary.Count;

            if (currentUserService.IsAuthenticated && currentUserService.UserId is { } userId)
            {
                IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Movie, id, cancellationToken);
                WatchedOnUtc = await movieWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
                CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Movie, id, cancellationToken);
            }

            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading movie details for id {MovieId}.", id);
            this.SetErrorToast("Could not fetch movie details from TheTVDB right now.");
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while loading movie details for id {MovieId}.", id);
            this.SetErrorToast("An unexpected error occurred.");
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleMovieLikeAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to like a movie.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            await likeRepository.ToggleAsync(userId, LikeTargetType.Movie, id, id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for movie {MovieId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleMovieWatchedAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to track watched movies.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            var isNowWatched = await movieWatchRepository.ToggleAsync(userId, id, cancellationToken);

            if (isNowWatched)
                this.SetSuccessToast("Movie marked as watched.");
            else
                this.SetInfoToast("Movie marked as not watched.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling watched state for movie {MovieId}.", id);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRateMovieAsync(
        [FromRoute] int id,
        [FromForm] int value,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to rate a movie.");
            return RedirectToPage(new { id });
        }

        if (value is < 1 or > 10)
            return RedirectToPage(new { id });

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            await ratingRepository.RateAsync(userId, RatingTargetType.Movie, id, id, value, cancellationToken);
            this.SetSuccessToast("Rating saved.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed rating movie {MovieId}.", id);
            this.SetErrorToast("Could not save your rating right now.");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostClearMovieRatingAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to rate a movie.");
            return RedirectToPage(new { id });
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            await ratingRepository.RemoveRatingAsync(userId, RatingTargetType.Movie, id, cancellationToken);
            this.SetInfoToast("Rating removed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed clearing rating for movie {MovieId}.", id);
            this.SetErrorToast("Could not update your rating right now.");
        }

        return RedirectToPage(new { id });
    }
}
