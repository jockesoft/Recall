using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
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

    /// <summary>
    /// Maps the extended episode DTO (from /episodes/{id}/extended) including
    /// score, content ratings, and awards.
    /// </summary>
    public static Episode ToDomain(this EpisodeExtendedDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Start from the base mapping, then layer in the extended fields.
        var baseEpisode = ((EpisodeDto)dto).ToDomain();

        return baseEpisode with
        {
            Score = dto.Score,
            ContentRatings = dto.ContentRatings?
                .Select(r => new EpisodeContentRating
                {
                    Name = r.Name,
                    Country = r.Country,
                    Description = r.Description,
                    FullName = r.FullName
                })
                .ToArray() ?? [],
            Awards = dto.Awards?
                .Select(a => new EpisodeAward
                {
                    Id = a.Id,
                    Name = a.Name,
                    Year = a.Year,
                    Category = a.Category,
                    IsWinner = a.IsWinner ?? false
                })
                .ToArray() ?? []
        };
    }
}