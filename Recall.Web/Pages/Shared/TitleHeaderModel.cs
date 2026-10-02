namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_TitleHeader.cshtml</c>, the top of Series/Details and
/// Movies/Details: poster, title, genres, a one-line summary, then the action
/// row and the rating. The page decides what the actions are; the partial
/// decides how they look and where they sit at each width.
/// </summary>
public sealed class TitleHeaderModel
{
    public required string Name { get; init; }

    public string? ImageUrl { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>"2008–2013 · Ended · 5 seasons" or "2023 · 3h 9m"; see <see cref="TitleSummary"/>.</summary>
    public string? Summary { get; init; }

    /// <summary>"series" or "movie": used in "Sign in to track this …".</summary>
    public required string Noun { get; init; }

    public required bool IsAuthenticated { get; init; }

    /// <summary>Where the sign-in card sends a visitor back to after signing in.</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>The one amber button: the next thing to do with this title.</summary>
    public TitleAction? Primary { get; init; }

    /// <summary>Shown in place of (or beside) the buttons when there is nothing to do: "Up to date".</summary>
    public TitleState? State { get; init; }

    public IReadOnlyList<TitleAction> Secondary { get; init; } = [];

    public LikeToggleModel? Like { get; init; }

    public RatingWidgetModel? Rating { get; init; }
}

/// <summary>A button in the title header's action row: a POST to a page handler.</summary>
public sealed class TitleAction
{
    public required string Label { get; init; }

    public required string Handler { get; init; }

    public required int RouteId { get; init; }

    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Icon class, from <c>Icons</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// True for a toggle that is currently on ("Watched Fri, Sep 11"): drawn as
    /// done rather than as a call to action, and pressing it turns it off.
    /// </summary>
    public bool IsOn { get; init; }
}

/// <summary>A quiet, non-interactive status in the action row.</summary>
public sealed record TitleState(string Text, string Icon);
