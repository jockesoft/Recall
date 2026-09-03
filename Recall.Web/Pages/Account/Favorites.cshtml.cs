using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class FavoritesModel(
    ICurrentUserService currentUser,
    IFavoritesService favoritesService,
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
}
