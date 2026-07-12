using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Mappings;

public static class SeasonMappings
{
    public static Season ToDomain(this SeasonDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new Season
        {
            Id = dto.Id,
            Image = dto.Image,
            ImageType = dto.ImageType,
            LastUpdated = dto.LastUpdated,
            Name = dto.Name,
            NameTranslations = dto.NameTranslations ?? [],
            Number = dto.Number,
            OverviewTranslations = dto.OverviewTranslations ?? [],
            SeriesId = dto.SeriesId,
            Type = dto.Type?.ToDomain(),
            Year = dto.Year
        };
    }
}