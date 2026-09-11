namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_RatingWidget" partial: a row of 1-10 pill buttons that
/// POSTs the clicked value to <see cref="RateHandler"/>, plus a "Clear" link
/// (shown only once rated) that POSTs to <see cref="ClearHandler"/>.
/// </summary>
public sealed class RatingWidgetModel
{
    /// <summary>Page handler name the picker posts to, e.g. "RateSeries" or "RateEpisode".</summary>
    public required string RateHandler { get; init; }

    /// <summary>Page handler name the "Clear" link posts to.</summary>
    public required string ClearHandler { get; init; }

    /// <summary>The [FromRoute] id the handlers expect (series id or episode id).</summary>
    public required int RouteId { get; init; }

    /// <summary>Extra values the handlers need, rendered as hidden fields.</summary>
    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();

    /// <summary>The current user's rating (1-10), or null when unrated.</summary>
    public int? CurrentValue { get; init; }

    /// <summary>Noun used in labels/aria text, e.g. "series" or "episode".</summary>
    public string TargetNoun { get; init; } = "item";
}
