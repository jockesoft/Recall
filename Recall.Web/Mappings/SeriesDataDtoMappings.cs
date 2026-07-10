using Recall.Web.Domain.TheTvDb;
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
            : (fallbackEpisodes?.ToList() ?? new List<EpisodeDto>());

        return new SeriesAggregate
        {
            TvdbId = dto.Id,
            Name = !string.IsNullOrWhiteSpace(translatedName) ? translatedName! : (dto.Name ?? string.Empty),
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
            Episodes = BuildEpisodeSummaries(episodesSource)
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

    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value, out var date) ? date : null;
    }
}