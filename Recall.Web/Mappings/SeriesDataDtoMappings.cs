using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Mappings;

public static class SeriesDataDtoMappings
{
    public static SeriesAggregate ToAggregate(
        this SeriesDataDto dto,
        SeriesTranslationDataDto? translation = null,
        IReadOnlyList<EpisodeDto>? fallbackEpisodes = null)
    {
        var translatedName = translation?.Name?.Trim();
        var translatedOverview = translation?.Overview?.Trim();

        var episodesSource = (dto.Episodes is { Count: > 0 })
            ? dto.Episodes
            : (fallbackEpisodes?.ToList() ?? []);

        var characters = (dto.Characters is { Count: > 0 })
            ? dto.Characters
            : [];
        
        return new SeriesAggregate
        {
            TvdbId = dto.Id,
            Name = !string.IsNullOrWhiteSpace(translatedName) ? translatedName : (dto.Name ?? string.Empty),
            Overview = !string.IsNullOrWhiteSpace(translatedOverview) ? translatedOverview : null,
            Slug = dto.Slug,
            ImageUrl = dto.Image,
            FirstAired = ParseDateOnly(dto.FirstAired),
            LastAired = ParseDateOnly(dto.LastAired),
            NextAired = ParseDateOnly(dto.NextAired),
            OriginalCountry = dto.OriginalCountry,
            OriginalLanguage = dto.OriginalLanguage,
            Score = dto.Score,
            Year = dto.Year,
            AverageRuntimeMinutes = dto.AverageRuntime,
            Status = dto.Status is null
                ? null
                : new SeriesStatus
                {
                    Id = dto.Status.Id,
                    Name = dto.Status.Name,
                    KeepUpdated = dto.Status.KeepUpdated,
                    RecordType = dto.Status.RecordType
                },
            Aliases = (dto.Aliases ?? [])
                .Select(a => a.Name?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Seasons = BuildDistinctSeasonSummaries(dto, episodesSource),
            Episodes = BuildEpisodeSummaries(episodesSource),
            Characters = BuildCharacters(characters)
        };
    }

    private static IReadOnlyList<SeasonSummary> BuildDistinctSeasonSummaries(
        SeriesDataDto dto,
        IReadOnlyList<EpisodeDto> episodesSource)
    {
        var fromTopLevel = (dto.Seasons ?? new List<SeasonDto>())
            .Where(s => s.Id.HasValue)
            .GroupBy(s => s.Id!.Value)
            .Select(g => g.First())
            .ToList();

        IEnumerable<SeasonDto> source = fromTopLevel.Count > 0
            ? fromTopLevel
            : episodesSource
                .SelectMany(e => e.Seasons ?? new List<SeasonDto>())
                .Where(s => s.Id.HasValue)
                .GroupBy(s => s.Id!.Value)
                .Select(g => g.First());

        return source
            .Select(s => new SeasonSummary
            {
                Id = s.Id!.Value,
                Number = s.Number,
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"Season {s.Number}" : s.Name!,
                ImageUrl = s.Image,
                Year = s.Year,
                TypeName = s.Type?.Name ?? s.Type?.Type,
                Studios = (s.Companies?.Studio ?? [])
                    .Select(c => c.Name?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Networks = (s.Companies?.Network ?? [])
                    .Select(c => c.Name?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .OrderBy(s => s.Number ?? int.MaxValue)
            .ThenBy(s => s.Id)
            .ToArray();
    }

    private static IReadOnlyList<EpisodeSummary> BuildEpisodeSummaries(IReadOnlyList<EpisodeDto> episodes)
    {
        return episodes
            .Where(e => e.Id.HasValue)
            .GroupBy(e => e.Id!.Value)
            .Select(g => g.First())
            .Select(e => new EpisodeSummary
            {
                Id = e.Id!.Value,
                SeasonNumber = e.SeasonNumber,
                EpisodeNumber = e.Number,
                Name = string.IsNullOrWhiteSpace(e.Name) ? $"Episode {e.Number}" : e.Name!,
                Overview = e.Overview,
                Aired = ParseDateOnly(e.Aired),
                RuntimeMinutes = e.Runtime,
                IsMovie = e.IsMovie.HasValue ? e.IsMovie.Value != 0 : null,
                FinaleType = e.FinaleType
            })
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ThenBy(e => e.Id)
            .ToArray();
    }

    private static Character[] BuildCharacters(IReadOnlyList<CharacterDataDto> characters)
    {
        return characters
            .Where(c => c.Id != 0)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .Select(c => new Character()
            {
                EpisodeId = c.EpisodeId,
                Id = c.Id,
                Image = c.Image,
                IsFeatured = c.IsFeatured ?? false,
                MovieId = c.MovieId,
                Name = c.Name,
                PeopleId = c.PeopleId,
                PeopleType = c.PeopleType,
                PersonName = c.PersonName,
                PersonImageUrl  = c.PersonImgUrl,
                SeriesId = c.SeriesId,
                Sort = c.Sort,
                Type = c.Type,
                Url = c.Url
            })
            .OrderBy(c => c.Id)
            .ThenBy(c => c.IsFeatured)
            .ThenBy(c => c.PersonName ?? string.Empty)
            .ToArray();
    }
    
    public static Character ToDomain(this CharacterDataDto dto)
    {
        return new Character
        {
            Id = dto.Id,
            Name = dto.Name,
            Image = dto.Image,
            IsFeatured = dto.IsFeatured ?? false,
            PeopleId = dto.PeopleId,
            PersonName = dto.PersonName,
            PersonImageUrl = dto.PersonImgUrl,
            PeopleType = dto.PeopleType,
            Type = dto.Type,
            Sort = dto.Sort,
            Url = dto.Url,

            Aliases = dto.Aliases?
                .Select(a => new CharacterAlias
                {
                    Language = a.Language,
                    Name = a.Name
                })
                .ToList() ?? [],

            NameTranslations = dto.NameTranslations ?? [],
            OverviewTranslations = dto.OverviewTranslations ?? [],

            EpisodeId = dto.EpisodeId,
            Episode = dto.Episode?.ToDomainRelatedItem(),

            MovieId = dto.MovieId,
            Movie = dto.Movie?.ToDomainRelatedItem(),

            SeriesId = dto.SeriesId,
            Series = dto.Series?.ToDomainRelatedItem(),

            TagOptions = dto.TagOptions?
                .Select(t => new CharacterTagOption
                {
                    Id = t.Id,
                    HelpText = t.HelpText,
                    Name = t.Name,
                    Tag = t.Tag,
                    TagName = t.TagName
                })
                .ToList() ?? new List<CharacterTagOption>()
        };
    }

    private static RelatedItem ToDomainRelatedItem(this CharacterRelatedItemDto dto)
    {
        return new RelatedItem
        {
            Name = dto.Name,
            Image = dto.Image,
            Year = dto.Year
        };
    }
    
    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value, out var date) ? date : null;
    }
}