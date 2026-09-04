using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class FavoritesModel(
    ICurrentUserService currentUser,
    IFavoritesService favoritesService,
    ILikeRepository likeRepository,
    ILogger<FavoritesModel> logger) : PageModel
{
    public IReadOnlyList<FavoriteSeries> Series { get; private set; } = Array.Empty<FavoriteSeries>();

    public IReadOnlyList<FavoriteEpisode> Episodes { get; private set; } = Array.Empty<FavoriteEpisode>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            var view = await favoritesService.GetAllFavoritesAsync(userId, cancellationToken);
            Series = view.Series;
            Episodes = view.Episodes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load favorites for the account page.");
        }
    }

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync(int id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            this.SetErrorToast("You need to be signed in to like a series.");
            return RedirectToPage();
        }

        try
        {
            await likeRepository.ToggleAsync(userId, LikeTargetType.Series, id, id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage();
    }
}
