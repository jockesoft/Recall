using System.Globalization;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Movies;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Mappings;

public static class MovieDataDtoMappings
{
    // TheTVDB artwork type id of a movie's 16:9 background (a series' is 3).
    private const int MovieBackgroundArtworkType = 15;

    /// <summary>
    /// The best-scored background, as its thumbnail (the full image is 1920
    /// wide, far more than the hero needs) or the full image when there is none.
    /// </summary>
    private static string? PickBackground(IReadOnlyList<ArtworkDto>? artworks) =>
        (artworks ?? [])
            .Where(a => a.Type == MovieBackgroundArtworkType)
            .OrderByDescending(a => a.Score ?? 0)
            .ThenBy(a => a.Id)
            .Select(a => !string.IsNullOrWhiteSpace(a.Thumbnail) ? a.Thumbnail : a.Image)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    /// <summary>
    /// The translation DTO shape (name/overview/tagline) is identical for series and
    /// movies on TheTVDB's API, so <see cref="SeriesTranslationDataDto"/> is reused here
    /// rather than duplicated.
    /// </summary>
    public static MovieAggregate ToAggregate(this MovieDataDto dto, SeriesTranslationDataDto? translation = null)
    {
        var translatedName = translation?.Name?.Trim();
        var translatedOverview = translation?.Overview?.Trim();

        var characters = dto.Characters is { Count: > 0 } ? dto.Characters : [];
        var remoteIds = dto.RemoteIds is { Count: > 0 } ? dto.RemoteIds : [];
        var genres = dto.Genres is { Count: > 0 } ? dto.Genres : [];
        var studios = dto.Companies?.Studio is { Count: > 0 } ? dto.Companies.Studio : [];

        return new MovieAggregate
        {
            TvdbId = dto.Id,
            Name = !string.IsNullOrWhiteSpace(translatedName) ? translatedName : (dto.Name ?? string.Empty),
            Overview = !string.IsNullOrWhiteSpace(translatedOverview) ? translatedOverview : null,
            Slug = dto.Slug,
            ImageUrl = ArtworkUrl.Normalize(dto.Image),
            BackgroundUrl = ArtworkUrl.Normalize(PickBackground(dto.Artworks)),
            ReleaseDate = ParseDateOnly(dto.FirstRelease?.Date),
            ReleaseDetail = string.IsNullOrWhiteSpace(dto.FirstRelease?.Detail) ? null : dto.FirstRelease.Detail.Trim(),
            Year = dto.Year,
            RuntimeMinutes = dto.Runtime,
            OriginalCountry = dto.OriginalCountry,
            OriginalLanguage = dto.OriginalLanguage,
            Score = dto.Score,
            Budget = ParsePositiveDecimal(dto.Budget),
            BoxOffice = ParsePositiveDecimal(dto.BoxOffice),
            Status = dto.Status is null
                ? null
                : new MovieStatus
                {
                    Id = dto.Status.Id,
                    Name = dto.Status.Name,
                    KeepUpdated = dto.Status.KeepUpdated,
                    RecordType = dto.Status.RecordType
                },
            Genres = genres
                .Select(g => g.Name?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Studios = studios
                .Select(c => c.Name?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Characters = characters
                .Where(c => c.Id != 0)
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .Select(c => c.ToDomain())
                .OrderBy(c => c.Sort ?? int.MaxValue)
                .ThenBy(c => c.PersonName ?? c.Name)
                .ToArray(),
            RemoteIds = remoteIds
                .Where(r => !string.IsNullOrWhiteSpace(r.SourceName) && !string.IsNullOrWhiteSpace(r.Id))
                .Select(r => new MovieRemoteId { SourceName = r.SourceName!, Id = r.Id!, Type = r.Type })
                .ToArray()
        };
    }

    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value, out var date) ? date : null;
    }

    private static decimal? ParsePositiveDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }
}
