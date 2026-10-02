using System.Globalization;

namespace Recall.Web.Services.Stats;

/// <summary>How the Stats page writes amounts.</summary>
public static class StatsFormat
{
    /// <summary>
    /// A length of time for a chart row: "45 min", "9 h 40 min", "9 h", and
    /// from a hundred hours up just the hours ("312 h"), where minutes are noise.
    /// </summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return "0 min";
        if (minutes < 60) return $"{minutes} min";

        var hours = minutes / 60;
        var rest = minutes % 60;

        return hours >= 100 || rest == 0 ? $"{Number(hours)} h" : $"{hours} h {rest} min";
    }

    /// <summary>Whole hours for the tightest spots (a label above a chart bar): "9 h", and "&lt;1 h" for less.</summary>
    public static string Hours(int minutes) =>
        minutes <= 0 ? "" : minutes < 60 ? "<1 h" : $"{Number((int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero))} h";

    /// <summary>"1 episode", "12 episodes".</summary>
    public static string Count(int count, string noun) => $"{Number(count)} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>A whole number with thousands separators: "1,500".</summary>
    public static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// A bar's length as a whole percentage of the largest value in its chart.
    /// Anything above zero gets at least 2, so a small value still shows.
    /// </summary>
    public static int Percent(int value, int largest)
    {
        if (value <= 0 || largest <= 0) return 0;
        return Math.Clamp((int)Math.Round(100.0 * value / largest), 2, 100);
    }
}
