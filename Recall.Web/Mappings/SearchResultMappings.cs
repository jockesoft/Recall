using System.Globalization;
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

        var (overview, overviewLanguage) = PickOverview(dto);

        return new SearchResultItem(
            dto.TvdbId,
            dto.Name ?? string.Empty,
            overview,
            ArtworkUrl.Normalize(dto.ImageUrl),
            dto.Year,
            type.Value,
            overviewLanguage);
    }

    private const string English = "eng";

    /// <summary>
    /// The English overview when there is one. Otherwise the title's own
    /// overview, with the language to mark it up in, so a screen reader
    /// pronounces it and a browser can offer to translate it.
    /// </summary>
    private static (string? Text, string? Language) PickOverview(SearchResultDto dto)
    {
        if (dto.Overviews is not null
            && dto.Overviews.TryGetValue(English, out var english)
            && !string.IsNullOrWhiteSpace(english))
        {
            return (english, null);
        }

        if (string.IsNullOrWhiteSpace(dto.Overview))
            return (null, null);

        var isEnglish = string.IsNullOrWhiteSpace(dto.PrimaryLanguage)
                        || string.Equals(dto.PrimaryLanguage, English, StringComparison.OrdinalIgnoreCase);

        return (dto.Overview, isEnglish ? null : ToLanguageTag(dto.PrimaryLanguage!));
    }

    // TheTVDB's codes are mostly ISO 639-2 ("deu"); a few are its own.
    private static readonly Dictionary<string, string> TvdbLanguageTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pt"] = "pt-BR",
        ["zhtw"] = "zh-Hant",
        ["yue"] = "yue"
    };

    private static readonly Lazy<Dictionary<string, string>> TwoLetterByThreeLetter = new(() =>
        CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(c => c.Name.Length is 2 or 3 && c.ThreeLetterISOLanguageName.Length == 3)
            .GroupBy(c => c.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A value for the HTML <c>lang</c> attribute: the two-letter code where the
    /// language has one ("deu" → "de"), TheTVDB's three-letter code where it
    /// doesn't (a valid tag on its own), and null for anything unrecognisable.
    /// </summary>
    public static string? ToLanguageTag(string tvdbLanguage)
    {
        var code = tvdbLanguage.Trim();

        if (TvdbLanguageTags.TryGetValue(code, out var known))
            return known;

        if (TwoLetterByThreeLetter.Value.TryGetValue(code, out var twoLetter))
            return twoLetter;

        return code.Length is 2 or 3 && code.All(char.IsAsciiLetter) ? code.ToLowerInvariant() : null;
    }
}
