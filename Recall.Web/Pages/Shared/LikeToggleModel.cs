namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_LikeToggle" partial: a small form whose submit button is
/// a heart that fills when liked. Posts to <see cref="Handler"/> and toggles the
/// like server-side.
/// </summary>
public sealed class LikeToggleModel
{
    /// <summary>Page handler name, e.g. "ToggleSeriesLike" or "ToggleLike".</summary>
    public required string Handler { get; init; }

    /// <summary>The [FromRoute] id the handler expects (series id or episode id).</summary>
    public required int RouteId { get; init; }

    /// <summary>Extra values the handler needs, rendered as hidden fields.</summary>
    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();

    public required bool IsLiked { get; init; }

    /// <summary>Noun used in the tooltip / aria-label, e.g. "series" or "episode".</summary>
    public string TargetNoun { get; init; } = "item";

    /// <summary>Where the heart sits, which decides its shape (see <see cref="LikePlacement"/>).</summary>
    public LikePlacement Placement { get; init; } = LikePlacement.Paper;
}

/// <summary>The heart's shape, by where it sits.</summary>
public enum LikePlacement
{
    /// <summary>On the cream panel (Episode Details): a 36px round button with a neutral ring.</summary>
    Paper,

    /// <summary>On a picture (a poster card): a 36px round button, dark with a light ring.</summary>
    Image,

    /// <summary>In a detail page's action row (_TitleActions): one of its 56px squares.</summary>
    ActionRow
}
