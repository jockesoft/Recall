using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Pages.Shared;

/// <summary>The one-line summary under a title: years, status, size.</summary>
public static class TitleSummary
{
    private const string Separator = " · ";

    /// <summary>"2008–2013 · Ended · 5 seasons"; "2022– · Continuing · 2 seasons" while it runs.</summary>
    public static string? ForSeries(SeriesAggregate series)
    {
        var parts = new List<string>();

        if (Years(series) is { } years)
            parts.Add(years);

        if (!string.IsNullOrWhiteSpace(series.Status?.Name))
            parts.Add(series.Status.Name);

        // Specials are extras, not a season.
        var seasons = series.Seasons.Select(s => s.Number).Where(n => n is > 0).Distinct().Count();
        if (seasons > 0)
            parts.Add(seasons == 1 ? "1 season" : $"{seasons} seasons");

        return parts.Count == 0 ? null : string.Join(Separator, parts);
    }

    /// <summary>"2023 · 3h 9m".</summary>
    public static string? ForMovie(MovieAggregate movie)
    {
        var parts = new List<string>();

        var year = movie.ReleaseDate?.Year.ToString() ?? movie.Year;
        if (!string.IsNullOrWhiteSpace(year))
            parts.Add(year);

        if (movie.RuntimeMinutes is > 0)
            parts.Add(Runtime(movie.RuntimeMinutes.Value));

        return parts.Count == 0 ? null : string.Join(Separator, parts);
    }

    /// <summary>"3h 9m", "45m".</summary>
    public static string Runtime(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours > 0 ? $"{hours}h {rest}m" : $"{rest}m";
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
