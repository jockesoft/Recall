using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.Favorites.Models;

/// <summary>Full favorites payload for the Account/Favorites page.</summary>
/// <param name="Titles">Liked series and movies, newest-liked first across both.</param>
/// <param name="TitleCount">How many titles (series + movies) the user has liked.</param>
/// <param name="Episodes">Liked episodes, newest-liked first.</param>
/// <param name="EpisodeCount">How many episodes the user has liked.</param>
public sealed record FavoritesView(
    IReadOnlyList<FavoriteTitle> Titles,
    int TitleCount,
    IReadOnlyList<FavoriteEpisode> Episodes,
    int EpisodeCount);

/// <summary>
/// A liked series or movie, shaped to feed the shared <c>_SeriesCard</c> partial
/// (via <c>SeriesCardModel</c>). Movies carry no episode progress, so
/// <see cref="WatchedEpisodes"/>/<see cref="ReleasedEpisodes"/> are both 0.
/// </summary>
public sealed record FavoriteTitle(
    SearchResultType Type,
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
