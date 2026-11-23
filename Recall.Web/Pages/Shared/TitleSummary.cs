using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Pages.Shared;

/// <summary>The one-line summary under a title: years, size, and for a series its network.</summary>
public static class TitleSummary
{
    private const string Separator = " · ";

    /// <summary>
    /// "2008–2013 · 5 seasons · AMC"; "2010– · 17 seasons · Discovery" while it
    /// runs. The status is not here: the hero shows it as a chip.
    /// </summary>
    public static string? ForSeries(SeriesAggregate series)
    {
        var parts = new List<string>();

        if (Years(series) is { } years)
            parts.Add(years);

        // Specials are extras, not a season.
        var seasons = series.Seasons.Select(s => s.Number).Where(n => n is > 0).Distinct().Count();
        if (seasons > 0)
            parts.Add(seasons == 1 ? "1 season" : $"{seasons} seasons");

        if (!string.IsNullOrWhiteSpace(series.Network))
            parts.Add(series.Network);

        return parts.Count == 0 ? null : string.Join(Separator, parts);
    }

    /// <summary>"2023 · 3 h 0 min · Universal Pictures": year, runtime, the first studio.</summary>
    public static string? ForMovie(MovieAggregate movie)
    {
        var parts = new List<string>();

        var year = movie.ReleaseDate?.Year.ToString() ?? movie.Year;
        if (!string.IsNullOrWhiteSpace(year))
            parts.Add(year);

        if (movie.RuntimeMinutes is > 0)
            parts.Add(Runtime(movie.RuntimeMinutes.Value));

        if (movie.Studios.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) is { } studio)
            parts.Add(studio);

        return parts.Count == 0 ? null : string.Join(Separator, parts);
    }

    /// <summary>A movie's length: "3 h 0 min", "2 h 50 min", "45 min".</summary>
    public static string Runtime(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours > 0 ? $"{hours} h {rest} min" : $"{rest} min";
    }

    private static string? Years(SeriesAggregate series)
    {
        if (series.FirstAired is not { } first)
            return string.IsNullOrWhiteSpace(series.Year) ? null : series.Year;

        // A series that is still being made has no end year yet.
        if (series.Status?.KeepUpdated == true || series.LastAired is not { } last)
            return $"{first.Year}–";

        return last.Year == first.Year ? first.Year.ToString() : $"{first.Year}–{last.Year}";
    }
}
