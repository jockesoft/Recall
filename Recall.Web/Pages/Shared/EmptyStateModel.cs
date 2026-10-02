namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_EmptyState.cshtml</c>: the one way a page says "there is
/// nothing here" — an icon, a heading, a sentence and, when there is an obvious
/// next step, one button (and at most one quieter second one).
/// </summary>
public sealed class EmptyStateModel
{
    /// <summary>Icon class, from <c>Icons</c>.</summary>
    public required string Icon { get; init; }

    public required string Heading { get; init; }

    public required string Text { get; init; }

    /// <summary>
    /// Heading level, 1–3. Use 1 when the empty state is the whole page (not
    /// found, error), 2 under a page's own h1, 3 inside a section.
    /// </summary>
    public int HeadingLevel { get; init; } = 2;

    /// <summary>Label of the optional button; requires <see cref="ActionPage"/>.</summary>
    public string? ActionText { get; init; }

    /// <summary>Razor page the button links to, e.g. "/Search".</summary>
    public string? ActionPage { get; init; }

    /// <summary>The <c>id</c> route value for <see cref="ActionPage"/>, when it needs one ("/Series/Details").</summary>
    public int? ActionRouteId { get; init; }

    /// <summary>Label of a second, quieter button beside the first; requires <see cref="SecondaryActionPage"/>.</summary>
    public string? SecondaryActionText { get; init; }

    /// <summary>Razor page the second button links to.</summary>
    public string? SecondaryActionPage { get; init; }

    /// <summary>Smaller padding, for an empty section rather than an empty page.</summary>
    public bool Inline { get; init; }
}
