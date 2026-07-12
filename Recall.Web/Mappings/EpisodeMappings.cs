using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Mappings;

public static class EpisodeMappings
{
    public static Episode ToDomain(this EpisodeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new Episode
        {
            AbsoluteNumber = dto.AbsoluteNumber,
            Aired = dto.Aired,
            AirsAfterSeason = dto.AirsAfterSeason,
            AirsBeforeEpisode = dto.AirsBeforeEpisode,
            AirsBeforeSeason = dto.AirsBeforeSeason,
            FinaleType = dto.FinaleType,
            Id = dto.Id,
            Image = dto.Image,
            ImageType = dto.ImageType,
            IsMovie = dto.IsMovie == 1,
            LastUpdated = dto.LastUpdated,
            LinkedMovie = dto.LinkedMovie,
            Name = dto.Name,
            NameTranslations = dto.NameTranslations ?? [],
            Number = dto.Number,
            Overview = dto.Overview,
            OverviewTranslations = dto.OverviewTranslations ?? [],
            Runtime = dto.Runtime,
            SeasonNumber = dto.SeasonNumber,
            Seasons = dto.Seasons?.Select(s => s.ToDomain()).ToList() ?? [],
            SeriesId = dto.SeriesId,
            SeasonName = dto.SeasonName,
            Year = dto.Year
        };
    }
}