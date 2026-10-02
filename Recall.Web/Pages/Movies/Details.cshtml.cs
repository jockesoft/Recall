using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Pages.Shared;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Movies;

/// <summary>
/// Public, anonymous-friendly movie details page. Watchlist/watched/like/rating
/// actions are only shown and only take effect when signed in.
/// </summary>
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.PublicDetailsPolicy)]
public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ILikeRepository likeRepository,
    IMovieWatchRepository movieWatchRepository,
    IMovieTrackingService movieTrackingService,
    IRatingRepository ratingRepository,
    IMovieOmdbSnapshotStore omdbSnapshotStore,
    TimeProvider timeProvider,
    ILogger<DetailsModel> logger)
    : PageModel
{
    /// <summary>Today's date in UTC, for the view's date format.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    public MovieAggregate? Movie { get; private set; }

    /// <summary>OMDb enrichment for this movie, when the background job has stored it.</summary>
    public OmdbMovie? Omdb { get; private set; }

    public bool IsAuthenticated => currentUserService.IsAuthenticated;

    /// <summary>Whether the current user has hearted this movie.</summary>
    public bool IsLikedByCurrentUser { get; private set; }

    /// <summary>When the current user marked this movie watched, or null if they haven't.</summary>
    public DateTime? WatchedOnUtc { get; private set; }

    public bool IsWatchedByCurrentUser => WatchedOnUtc is not null;

    /// <summary>Whether the movie is on the current user's watchlist. Never true for a watched movie.</summary>
    public bool IsOnWatchlist { get; private set; }

    /// <summary>The current user's 1-10 rating of this movie, or null when unrated.</summary>
    public int? CurrentUserRating { get; private set; }

    /// <summary>Average of every Recall user's rating of this movie, when at least one exists.</summary>
    public double? RecallRatingAverage { get; private set; }

    /// <summary>How many Recall users have rated this movie.</summary>
    public int RecallRatingCount { get; private set; }

    public IReadOnlyList<CastPerson> Cast { get; private set; } = [];

    public IReadOnlyList<CastPerson> Crew { get; private set; } = [];

    /// <summary>
    /// The top of the page (see <c>_TitleHeader.cshtml</c>): "Mark as watched"
    /// is the primary button, the watchlist is secondary, and a movie that is
    /// already on the watchlist says so quietly (removing it is at the bottom
    /// of the details).
    /// </summary>
    public TitleHeaderModel BuildHeader(string? returnUrl = null)
    {
        var movie = Movie!;

        var primary = new TitleAction
        {
            Label = WatchedOnUtc is { } watched ? $"Watched {DisplayDate.Format(watched, Today)}" : "Mark as watched",
            Handler = "ToggleMovieWatched",
            RouteId = movie.TvdbId,
            Icon = Icons.Check,
            IsOn = IsWatchedByCurrentUser
        };

        // A watched movie can't be on the watchlist, so the choice isn't offered.
        var canAddToWatchlist = !IsWatchedByCurrentUser && !IsOnWatchlist;

        return new TitleHeaderModel
        {
            Name = movie.Name,
            ImageUrl = movie.ImageUrl,
            Genres = movie.Genres,
            Summary = TitleSummary.ForMovie(movie),
            Noun = "movie",
            IsAuthenticated = IsAuthenticated,
            ReturnUrl = returnUrl,
            Primary = primary,
            State = IsOnWatchlist ? new TitleState("On your watchlist", Icons.OnWatchlist) : null,
            Secondary = canAddToWatchlist
                ? [new TitleAction { Label = "Add to watchlist", Handler = "ToggleWatchlist", RouteId = movie.TvdbId, Icon = Icons.Watchlist }]
                : [],
            Like = new LikeToggleModel
            {
                Handler = "ToggleMovieLike",
                RouteId = movie.TvdbId,
                IsLiked = IsLikedByCurrentUser,
                TargetNoun = "movie"
            },
            Rating = new RatingWidgetModel
            {
                RateHandler = "RateMovie",
                ClearHandler = "ClearMovieRating",
                RouteId = movie.TvdbId,
                CurrentValue = CurrentUserRating,
                TargetNoun = "movie"
            }
        };
    }

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

            (Cast, Crew) = CastBuilder.Build(Movie.Characters);

            if (currentUserService.TryGetUserId(out var userId))
            {
                IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Movie, id, cancellationToken);
                WatchedOnUtc = await movieWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
                IsOnWatchlist = await movieTrackingService.IsOnWatchlistAsync(userId, id, cancellationToken);
                CurrentUserRating = await ratingRepository.GetRatingAsync(userId, RatingTargetType.Movie, id, cancellationToken);
            }

            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading movie details for id {MovieId}.", id);
            return LoadFailed(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while loading movie details for id {MovieId}.", id);
            return LoadFailed(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>The page renders its own "couldn't load" state, with a status code that says so.</summary>
    private IActionResult LoadFailed(int statusCode)
    {
        Movie = null;
        Response.StatusCode = statusCode;
        return Page();
    }

    public async Task<IActionResult> OnPostToggleMovieLikeAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "like a movie");

        try
        {
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
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "track watched movies");

        try
        {
            // Through the service, not the repository: marking watched also
            // takes the movie off the watchlist.
            var isNowWatched = await movieTrackingService.ToggleWatchedAsync(userId, id, cancellationToken);

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

    public async Task<IActionResult> OnPostToggleWatchlistAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "use your watchlist");

        try
        {
            if (await movieTrackingService.RemoveFromWatchlistAsync(userId, id, cancellationToken))
            {
                this.SetInfoToast("Removed from your watchlist.");
                return RedirectToPage(new { id });
            }

            switch (await movieTrackingService.AddToWatchlistAsync(userId, id, cancellationToken: cancellationToken))
            {
                case MovieWatchlistOutcome.Added:
                case MovieWatchlistOutcome.AlreadyOnWatchlist:
                    this.SetSuccessToast("Added to your watchlist.");
                    break;
                case MovieWatchlistOutcome.AlreadyWatched:
                    this.SetInfoToast("You've already watched this movie.");
                    break;
                case MovieWatchlistOutcome.MovieNotFound:
                    this.SetErrorToast("Could not find that movie on TheTVDB.");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling the watchlist for movie {MovieId}.", id);
            this.SetErrorToast("Could not update your watchlist right now.");
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRateMovieAsync(
        [FromRoute] int id,
        [FromForm] int value,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate a movie");

        if (value is < 1 or > 10)
            return RedirectToPage(new { id });

        try
        {
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
        if (!currentUserService.TryGetUserId(out var userId))
            return SignInRequired(id, "rate a movie");

        try
        {
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

    /// <summary>
    /// What every POST handler here returns to an anonymous caller: the page is
    /// public, so <c>[Authorize]</c> can't guard its handlers.
    /// </summary>
    private IActionResult SignInRequired(int id, string toDoWhat)
    {
        this.SetErrorToast($"You need to be signed in to {toDoWhat}.");
        return RedirectToPage(new { id });
    }
}
