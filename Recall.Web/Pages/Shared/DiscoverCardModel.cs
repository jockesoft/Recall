using System.Globalization;
using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// One Discover card (<c>_DiscoverCard</c>): a 150px poster with the name and
/// "Series · 2026" under it, linking to the title's page, and on the poster
/// the round "+" (a series: add to the library) or bookmark (a movie: add to
/// the watchlist) and the "×" (not interested). The card is named by its row
/// and place ("discover-new-5", <see cref="ListPosition"/>), so a POST from
/// it returns to the same place.
/// </summary>
public sealed class DiscoverCardModel
{
    public required DiscoverTitle Title { get; init; }

    /// <summary>"discover-new-5".</summary>
    public required string ElementId { get; init; }

    /// <summary>The last card of its row: the forms say so (<c>last</c>), so the page returns to the new last one.</summary>
    public bool IsLastInRow { get; init; }

    /// <summary>Load the poster at once (the first cards of the first row); the rest load lazily.</summary>
    public bool LoadEagerly { get; init; }

    public bool IsMovie => Title.Kind == DiscoverKind.Movie;

    public string DetailsPage => IsMovie ? "/Movies/Details" : "/Series/Details";

    /// <summary>"Series · 2026", "Movie · 2026", or just the type without a year.</summary>
    public string MetaLine
    {
        get
        {
            var type = IsMovie ? "Movie" : "Series";
            var year = Title.FirstAired?.Year ?? Title.Year;
            return year is { } y ? $"{type} · {y.ToString(CultureInfo.InvariantCulture)}" : type;
        }
    }

    /// <summary>The add button's accessible name.</summary>
    public string AddLabel => IsMovie ? $"Add {Title.Name} to your watchlist" : $"Add {Title.Name} to your library";

    /// <summary>The add button's tooltip.</summary>
    public string AddHint => IsMovie ? "Add to watchlist" : "Add to library";

    /// <summary>The "×" button's accessible name; its tooltip is "Not interested".</summary>
    public string HideLabel => $"Not interested in {Title.Name}";

    /// <summary>What the forms post as <c>kind</c>.</summary>
    public string Kind => Title.Kind.ToString();
}
