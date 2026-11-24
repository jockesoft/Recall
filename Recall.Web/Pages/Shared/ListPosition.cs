using System.Globalization;
using System.Text.RegularExpressions;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// Where a one-tap mark (or a stop) in a list returns to: the same place in
/// the list, not the series' new place. The cards of Continue watching on the
/// Dashboard and of Watching in the Library are named by their place, 1-based
/// ("catchup-card-5", "catchup-row-5", "watching-card-5"), not by their
/// series, and a mark comes back to the place it was pressed at, which is
/// then taken by another series: the one before it when the marked series has
/// moved to the top of the list (it has just been watched), the one after it
/// when the marked series has left the list (caught up, or stopped). So
/// ticking one series after another never sends the page back to the top.
/// </summary>
public static partial class ListPosition
{
    /// <summary>The element id of the item at <paramref name="position"/> (1-based): "catchup-card-5".</summary>
    public static string Id(string prefix, int position) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}-{position}");

    /// <summary>
    /// The fragment to return to after an action on the item <paramref name="anchor"/>
    /// names ("catchup-card-5"), or null when it is not one of <paramref name="prefixes"/>
    /// with a position.
    /// </summary>
    /// <param name="leftTheList">The action took the item out of the list (caught up, stopped).</param>
    /// <param name="wasLast">The item was the last of the list: when it has left, its place is gone, so the page returns to the new last one.</param>
    /// <param name="emptyFallback">Where to return when the item was the only one and has left (the section), or null.</param>
    public static string? ReturnTo(string? anchor, IReadOnlyCollection<string> prefixes, bool leftTheList, bool wasLast, string? emptyFallback)
    {
        if (anchor is null || PositionPattern().Match(anchor) is not { Success: true } match)
            return null;

        var prefix = match.Groups["prefix"].Value;
        if (!prefixes.Contains(prefix)
            || !int.TryParse(match.Groups["position"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            || position < 1)
            return null;

        if (!leftTheList || !wasLast)
            return Id(prefix, position);

        return position > 1 ? Id(prefix, position - 1) : emptyFallback;
    }

    [GeneratedRegex(@"^(?<prefix>[a-z]+(?:-[a-z]+)*)-(?<position>\d{1,6})$")]
    private static partial Regex PositionPattern();
}
