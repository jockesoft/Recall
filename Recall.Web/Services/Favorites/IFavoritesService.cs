using Recall.Web.Services.Favorites.Models;

namespace Recall.Web.Services.Favorites;

/// <summary>
/// Assembles a user's "liked" (hearted) series and episodes into view-ready
/// shapes, pulling names / art / numbering from the layered-cached TheTVDB
/// aggregates. Backs the Account/Profile "Favorites" block and the dedicated
/// Account/Favorites page.
/// </summary>
public interface IFavoritesService
{
    /// <summary>
    /// The user's liked series as poster-card data, newest-liked first. A
    /// <paramref name="limit"/> of <c>null</c> returns all of them. Series whose
    /// data can't be loaded are skipped.
    /// </summary>
    Task<IReadOnlyList<FavoriteSeries>> GetLikedSeriesAsync(
        Guid userId,
        int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything for the Account/Favorites page: all liked series and all liked
    /// episodes, each newest-liked first, with totals.
    /// </summary>
    Task<FavoritesView> GetAllFavoritesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
