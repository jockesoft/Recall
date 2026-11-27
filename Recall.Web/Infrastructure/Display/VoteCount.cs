using System.Globalization;
using Recall.Web.Infrastructure.Hosting;

namespace Recall.Web.Infrastructure.Display;

/// <summary>
/// IMDb's vote count as OMDb gives it, written with thousands separators in
/// the app's culture (<see cref="AppCulture"/>, "16,180"), named here so it
/// does not depend on the thread's. OMDb sends it as text, with separators for
/// series and movies ("315,074") and without for episodes ("16180"), and "N/A"
/// when it has none.
/// </summary>
public static class VoteCount
{
    /// <summary>"16,180" for "16180" or "16,180"; null for nothing or "N/A"; anything else unparsed, as given.</summary>
    public static string? Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        if (string.Equals(text, "N/A", StringComparison.OrdinalIgnoreCase))
            return null;

        // OMDb's own separators are commas, whatever the app's culture.
        return long.TryParse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var votes) && votes >= 0
            ? votes.ToString("N0", CultureInfo.GetCultureInfo(AppCulture.Name))
            : text;
    }
}
