namespace Recall.Web.Services.Favorites.Models;

/// <summary>Full favorites payload for the Account/Favorites page.</summary>
/// <param name="Series">Liked series, newest-liked first.</param>
/// <param name="SeriesCount">How many series the user has liked.</param>
/// <param name="Episodes">Liked episodes, newest-liked first.</param>
/// <param name="EpisodeCount">How many episodes the user has liked.</param>
public sealed record FavoritesView(
    IReadOnlyList<FavoriteSeries> Series,
    int SeriesCount,
    IReadOnlyList<FavoriteEpisode> Episodes,
    int EpisodeCount);

/// <summary>
/// A liked series, shaped to feed the shared <c>_SeriesCard</c> partial
/// (via <c>SeriesCardModel</c>).
/// </summary>
public sealed record FavoriteSeries(
    int TvdbId,
    string Name,
    string? ImageUrl,
    DateOnly? FirstAired,
    int WatchedEpisodes,
    int ReleasedEpisodes);

/// <summary>
/// A liked episode, shaped for the compact <c>_FavoriteEpisodeRow</c> partial:
/// the parent <see cref="SeriesName"/> in bold, then "S02E03 · Episode title".
/// </summary>
public sealed record FavoriteEpisode(
    int TvdbId,
    string SeriesName,
    string? ImageUrl,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeName);
