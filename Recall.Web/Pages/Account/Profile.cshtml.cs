using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class ProfileModel(
    ICurrentUserService currentUser,
    IWatchTimeService watchTimeService,
    IFavoritesService favoritesService,
    ILikeRepository likeRepository,
    ILogger<ProfileModel> logger) : PageModel
{
    /// <summary>How many liked series the profile page previews before the "see all" arrow.</summary>
    public const int FavoritesPreviewCount = 6;

    public string DisplayName => currentUser.DisplayName ?? "—";

    public string Email => currentUser.Email ?? "—";

    public WatchTimeSummary WatchTime { get; private set; } = WatchTimeSummary.Empty;

    public IReadOnlyList<FavoriteSeries> FavoriteSeries { get; private set; } = Array.Empty<FavoriteSeries>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            WatchTime = await watchTimeService.GetTotalWatchTimeAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compute total watch time for the profile page.");
        }

        try
        {
            FavoriteSeries = await favoritesService.GetLikedSeriesAsync(userId, FavoritesPreviewCount, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load favorite series for the profile page.");
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
