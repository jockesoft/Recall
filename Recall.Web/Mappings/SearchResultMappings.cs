using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Mappings;

public static class SearchResultMappings
{
    /// <summary>
    /// TheTVDB's unscoped search returns series, movies, people, companies, etc.
    /// all mixed together — this maps only the two content types Recall tracks
    /// and drops the rest (returns null).
    /// </summary>
    public static SearchResultItem? ToDomain(this SearchResultDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        SearchResultType? type = dto.Type?.ToLowerInvariant() switch
        {
            "series" => SearchResultType.Series,
            "movie" => SearchResultType.Movie,
            _ => null
        };

        if (type is null)
            return null;

        return new SearchResultItem(
            dto.TvdbId,
            dto.Name ?? string.Empty,
            dto.Overview,
            ArtworkUrl.Normalize(dto.ImageUrl),
            dto.Year,
            type.Value);
    }
}
