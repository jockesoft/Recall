using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Movies;

/// <summary>
/// Public, anonymous-friendly movie details page. Like/watched actions are only
/// shown when signed in; no library/tracking or ratings yet, those need their
/// own data model.
/// </summary>
public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ILikeRepository likeRepository,
    IMovieWatchRepository movieWatchRepository,
    ILogger<DetailsModel> logger)
    : PageModel
{
    public MovieAggregate? Movie { get; private set; }

    public bool IsAuthenticated => currentUserService.IsAuthenticated;

    /// <summary>Whether the current user has hearted this movie.</summary>
    public bool IsLikedByCurrentUser { get; private set; }

    /// <summary>When the current user marked this movie watched, or null if they haven't.</summary>
    public DateTime? WatchedOnUtc { get; private set; }

    public bool IsWatchedByCurrentUser => WatchedOnUtc is not null;

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Movie = await theTvDbService.GetMovieAggregateByIdAsync(id, cancellationToken);
            if (Movie is null) return NotFound();

            if (currentUserService.IsAuthenticated && currentUserService.UserId is { } userId)
            {
                IsLikedByCurrentUser = await likeRepository.IsLikedAsync(userId, LikeTargetType.Movie, id, cancellationToken);
                WatchedOnUtc = await movieWatchRepository.GetWatchedUtcAsync(userId, id, cancellationToken);
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
}
