using System.Globalization;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services.Stats;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_TitleHero.cshtml</c>: the top of a detail page, on the dark
/// frame above the panel. The title's background art behind a shade, the way
/// back, the poster beside the chips, the title, a meta line and the ratings,
/// the user's progress, and the action row (<see cref="TitleActionsModel"/>).
/// Series Details today; Movie Details is to use it too.
/// </summary>
public sealed class TitleHeroModel
{
    public required string Name { get; init; }

    public string? PosterUrl { get; init; }

    /// <summary>16:9 art behind the hero, decorative. Null: a plain dark gradient.</summary>
    public string? BackgroundUrl { get; init; }

    public required TitleBackLink Back { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>"Continuing" or "Ended", beside the genres.</summary>
    public TitleStatusChip? Status { get; init; }

    /// <summary>"2010– · 17 seasons · Discovery".</summary>
    public string? Meta { get; init; }

    /// <summary>IMDb's rating as OMDb gives it ("7.3"), when known.</summary>
    public string? ImdbRating { get; init; }

    /// <summary>The average of Recall users' ratings ("9.0"), when anyone has rated it.</summary>
    public string? RecallRating { get; init; }

    public int RecallRatingCount { get; init; }

    /// <summary>The user's own rating, behind the "Your rating" chip; null for a visitor.</summary>
    public RatingWidgetModel? Rating { get; init; }

    /// <summary>Only for a title in the user's library with something released.</summary>
    public TitleProgressModel? Progress { get; init; }

    public required TitleActionsModel Actions { get; init; }

    /// <summary>"Your rating 9", or "Rate" while unrated.</summary>
    public string RatingChipText => Rating?.CurrentValue is { } value
        ? $"Your rating {value.ToString(CultureInfo.InvariantCulture)}"
        : "Rate";
}

/// <summary>The way back at the top of the hero.</summary>
/// <param name="UseHistory">Go back in the browser's history when there is any, rather than to <paramref name="Page"/>.</param>
public sealed record TitleBackLink(string Text, string Page, bool UseHistory);

/// <summary>TheTVDB's status of a series. <paramref name="Active"/>: still being made (green).</summary>
public sealed record TitleStatusChip(string Text, bool Active);

/// <summary>
/// "342 of 343 episodes · 99%", "1 left · 1 h 22 min" and "Last watched Sun,
/// Nov 22" in the hero. The counts are released regular episodes (specials
/// never count; see <c>WatchProgressCalculator</c>).
/// </summary>
public sealed record TitleProgressModel(
    int WatchedCount,
    int ReleasedCount,
    int? RemainingMinutes,
    string? LastWatched)
{
    public int LeftCount => Math.Max(ReleasedCount - WatchedCount, 0);

    /// <summary>Whole percent, rounded down: 342 of 343 is 99, and only everything is 100.</summary>
    public int Percent => ReleasedCount <= 0 ? 0 : (int)Math.Floor(100.0 * WatchedCount / ReleasedCount);

    public string CountText =>
        $"{StatsFormat.Number(WatchedCount)} of {StatsFormat.Count(ReleasedCount, "episode")} · {Percent}%";

    /// <summary>"1 left · 1 h 22 min"; just "3 left" when no runtime is known; "All watched".</summary>
    public string LeftText => LeftCount == 0
        ? "All watched"
        : RemainingMinutes is > 0 and var minutes
            ? $"{StatsFormat.Number(LeftCount)} left · {StatsFormat.Duration(minutes)}"
            : $"{StatsFormat.Number(LeftCount)} left";

    public string LastWatchedText => LastWatched is null ? "Not started yet" : $"Last watched {LastWatched}";
}

/// <summary>
/// Input for <c>_TitleActions.cshtml</c>: the action row of a detail page.
/// One full-width primary button (or, when there is nothing to do, a quiet
/// status box), then three square icon buttons: a "⋯" menu, the heart and
/// Share. A visitor gets the sign-in card instead of the whole row.
/// </summary>
public sealed class TitleActionsModel
{
    public required bool IsAuthenticated { get; init; }

    /// <summary>"series" or "movie": "Sign in to track this series".</summary>
    public required string Noun { get; init; }

    /// <summary>Where the sign-in card sends a visitor back to after signing in.</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>A sentence that explains the primary button and comes before it ("You stopped watching on Fri, Sep 4").</summary>
    public TitleNotice? Notice { get; init; }

    /// <summary>The one amber button. Null when <see cref="Status"/> is shown instead.</summary>
    public TitleAction? Primary { get; init; }

    /// <summary>"Up to date" and what comes next, in place of a button.</summary>
    public TitleStatusBox? Status { get; init; }

    /// <summary>The "⋯" menu: secondary actions. Not drawn when empty.</summary>
    public IReadOnlyList<TitleMenuItem> Menu { get; init; } = [];

    /// <summary>The menu button's accessible name: "More actions for Breaking Bad".</summary>
    public required string MenuLabel { get; init; }

    public LikeToggleModel? Like { get; init; }

    /// <summary>The name shared with the page's address (the canonical URL, read from the page).</summary>
    public required string ShareTitle { get; init; }
}

/// <summary>A sentence before the primary button.</summary>
public sealed record TitleNotice(string Text, string Icon);

/// <summary>
/// The quiet box in place of the primary button: an icon, a line, and a
/// second line that may end in a release time ("S17E09 airs" then the
/// <c>&lt;time&gt;</c> of <paramref name="Release"/>).
/// </summary>
public sealed record TitleStatusBox(string Text, string Icon, string? Detail = null, ReleaseTimeModel? Release = null);

/// <summary>
/// One entry in the "⋯" menu: a POST to a page handler, or (with
/// <see cref="ModalId"/>) a button that opens a confirmation first.
/// </summary>
public sealed class TitleMenuItem
{
    public required string Label { get; init; }

    public required string Icon { get; init; }

    public string? Handler { get; init; }

    public int RouteId { get; init; }

    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();

    /// <summary>The confirmation (<c>_ConfirmModal</c>) this entry opens instead of posting.</summary>
    public string? ModalId { get; init; }

    /// <summary>The button's id, for a test or a script to find it.</summary>
    public string? Id { get; init; }
}
