using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Mappings;

public static class SeriesMapping
{
    public static Series ToDomain(this SeriesDataDto dto) => dto.ToMapper();

    public static Series ToMapper(this SeriesDataDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new Series
        {
            Aliases = dto.Aliases?.Select(alias => alias.ToMapper()).ToArray() ?? [],
            AverageRuntime = dto.AverageRuntime,
            Country = dto.Country,
            DefaultSeasonType = dto.DefaultSeasonType,
            Episodes = dto.Episodes?.Select(episode => episode.ToDomain()).ToArray() ?? [],
            FirstAired = dto.FirstAired,
            Id = dto.Id,
            Image = "https://artworks.thetvdb.com" + dto.Image,
            IsOrderRandomized = dto.IsOrderRandomized,
            LastAired = dto.LastAired,
            LastUpdated = dto.LastUpdated,
            Name = dto.Name,
            NameTranslations = dto.NameTranslations ?? [],
            NextAired = dto.NextAired,
            OriginalCountry = dto.OriginalCountry,
            OriginalLanguage = dto.OriginalLanguage,
            OverviewTranslations = dto.OverviewTranslations ?? [],
            Score = dto.Score,
            Slug = dto.Slug,
            Status = dto.Status?.ToMapper(),
            Year = dto.Year,
            Seasons = dto.Seasons?.Select(season => season.ToDomain()).ToArray() ?? []
        };
    }

    private static SeriesAlias ToMapper(this AliasDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new SeriesAlias
        {
            Language = dto.Language,
            Name = dto.Name
        };
    }

    private static SeriesStatusInfo ToMapper(this StatusDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new SeriesStatusInfo
        {
            Id = dto.Id,
            KeepUpdated = dto.KeepUpdated,
            Name = dto.Name,
            RecordType = dto.RecordType
        };
    }
}
