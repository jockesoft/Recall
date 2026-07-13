namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_EpisodeWatchedToggle" partial. Renders a small form that
/// posts to the given page handler, works for both the current page's own
/// [FromRoute] id (RouteId) and any extra [FromForm]/[BindProperty] values the
/// handler needs (HiddenFields) — e.g. episodeId + season on Series/Details,
/// where the handler's own route id is the *series* id, not the episode id.
/// </summary>
public sealed class EpisodeWatchedToggleModel
{
    /// <summary>Page handler name, e.g. "ToggleWatched" or "ToggleEpisodeWatched".</summary>
    public required string Handler { get; init; }

    /// <summary>
    /// The [FromRoute] id the handler expects. Must travel via the URL path
    /// (asp-route-id), not a hidden field — [FromRoute] binding only reads
    /// from route values, so a hidden form field here would silently fail to bind.
    /// </summary>
    public required int RouteId { get; init; }

    /// <summary>
    /// Any other values the handler needs that are NOT the route id — e.g. an
    /// episodeId that's [FromForm], or a Season filter to preserve across the
    /// redirect. Rendered as hidden form fields, which satisfies both
    /// [FromForm] and ordinary [BindProperty] binding.
    /// </summary>
    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();

    public required bool IsWatched { get; init; }

    /// <summary>
    /// Compact = small circular checkmark toggle for dense list rows.
    /// Non-compact = full pill button, as used on the episode detail page.
    /// </summary>
    public bool Compact { get; init; }
}
