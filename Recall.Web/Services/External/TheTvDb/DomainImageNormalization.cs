using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// Defensive re-normalization applied to every <see cref="SeriesAggregate"/>,
/// <see cref="MovieAggregate"/> and <see cref="Episode"/> <see cref="TheTvDbService"/>
/// hands back, regardless of which tier (Redis, the Postgres snapshot, or a fresh
/// API fetch) produced it.
///
/// Fresh fetches are already normalized at the DTO→domain mapping layer
/// (<c>SeriesDataDtoMappings</c>/<c>EpisodeMappings</c>), but <c>GetLayeredAsync</c>'s
/// Postgres tier has no staleness check — a row cached before that normalization
/// existed (or before any future mapping bug is fixed) is treated as good
/// forever, and would otherwise keep serving broken relative image paths
/// indefinitely. Re-applying <see cref="ArtworkUrl.Normalize"/> here is a cheap,
/// idempotent no-op for anything already absolute, so it's safe to run on every
/// read.
/// </summary>
public static class DomainImageNormalization
{
    public static SeriesAggregate WithNormalizedImages(this SeriesAggregate aggregate) =>
        aggregate with
        {
            ImageUrl = ArtworkUrl.Normalize(aggregate.ImageUrl),
            Seasons = aggregate.Seasons.Select(NormalizeSeason).ToArray(),
            Episodes = aggregate.Episodes.Select(NormalizeEpisodeSummary).ToArray(),
            Characters = aggregate.Characters.Select(NormalizeCharacter).ToArray()
        };

    public static Episode WithNormalizedImages(this Episode episode) =>
        episode with { Image = ArtworkUrl.Normalize(episode.Image) };

    public static MovieAggregate WithNormalizedImages(this MovieAggregate aggregate) =>
        aggregate with
        {
            ImageUrl = ArtworkUrl.Normalize(aggregate.ImageUrl),
            Characters = aggregate.Characters.Select(NormalizeCharacter).ToArray()
        };

    private static SeasonSummary NormalizeSeason(SeasonSummary season) => new()
    {
        Id = season.Id,
        Number = season.Number,
        Name = season.Name,
        ImageUrl = ArtworkUrl.Normalize(season.ImageUrl),
        Year = season.Year,
        TypeName = season.TypeName,
        Studios = season.Studios,
        Networks = season.Networks
    };

    private static EpisodeSummary NormalizeEpisodeSummary(EpisodeSummary episode) => new()
    {
        Id = episode.Id,
        SeasonNumber = episode.SeasonNumber,
        EpisodeNumber = episode.EpisodeNumber,
        Name = episode.Name,
        Overview = episode.Overview,
        Image = ArtworkUrl.Normalize(episode.Image),
        Aired = episode.Aired,
        RuntimeMinutes = episode.RuntimeMinutes,
        IsMovie = episode.IsMovie,
        FinaleType = episode.FinaleType
    };

    private static Character NormalizeCharacter(Character character) => new()
    {
        Id = character.Id,
        Name = character.Name,
        Image = ArtworkUrl.Normalize(character.Image),
        IsFeatured = character.IsFeatured,
        PeopleId = character.PeopleId,
        PersonName = character.PersonName,
        PersonImageUrl = ArtworkUrl.Normalize(character.PersonImageUrl),
        PeopleType = character.PeopleType,
        Type = character.Type,
        Sort = character.Sort,
        Url = character.Url,
        EpisodeId = character.EpisodeId,
        Episode = character.Episode,
        MovieId = character.MovieId,
        Movie = character.Movie,
        SeriesId = character.SeriesId,
        Series = character.Series,
        Aliases = character.Aliases,
        NameTranslations = character.NameTranslations,
        OverviewTranslations = character.OverviewTranslations,
        TagOptions = character.TagOptions
    };
}
