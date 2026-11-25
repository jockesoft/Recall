using System.Globalization;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Filter;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Mappings;

public static class DiscoverTitleMappings
{
    /// <summary>
    /// A filter result as a Discover title, or null when it has no id or no
    /// name. The poster is normalized here (a movie's is a relative path);
    /// whether the title is good enough to show is decided later
    /// (<c>DiscoverQuality</c>).
    /// </summary>
    public static DiscoverTitle? ToDiscoverTitle(this FilterRecordDto dto, DiscoverKind kind)
    {
        if (dto.Id <= 0 || string.IsNullOrWhiteSpace(dto.Name))
            return null;

        return new DiscoverTitle
        {
            Kind = kind,
            TvdbId = dto.Id,
            Name = dto.Name.Trim(),
            Year = int.TryParse(dto.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null,
            ImageUrl = ArtworkUrl.Normalize(dto.Image),
            FirstAired = DateOnly.TryParseExact(dto.FirstAired, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired)
                ? aired
                : null,
            Status = string.IsNullOrWhiteSpace(dto.Status?.Name) ? null : dto.Status.Name,
            Score = dto.Score ?? 0
        };
    }
}
