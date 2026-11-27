using System.Globalization;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services.Stats;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_TitleHero.cshtml</c>: the top of a detail page, on the dark
/// frame above the panel. The title's background art behind a shade, the way
/// back, the poster beside the chips, the title, a meta line and the ratings,
/// the user's progress, and the action row (<see cref="TitleActionsModel"/>).
/// Series Details, Movie Details and Episode Details. An episode has no
/// poster (<see cref="ShowPoster"/>); it has its series above the title
/// (<see cref="Parent"/>), where it is in its season (<see cref="Position"/>)
/// and the episode after it (<see cref="UpNext"/>).
/// </summary>
public sealed class TitleHeroModel
{
    public required string Name { get; init; }

    public string? PosterUrl { get; init; }

    /// <summary>
    /// False for an episode, which has no poster of its own: the heading block
    /// then keeps the poster's height, so the hero is as tall as a series'.
    /// </summary>
    public bool ShowPoster { get; init; } = true;

    /// <summary>What the title belongs to, as a link above it: an episode's series.</summary>
    public TitleParentLink? Parent { get; init; }

    /// <summary>16:9 art behind the hero, decorative. Null: a plain dark gradient.</summary>
    public string? BackgroundUrl { get; init; }

    public required TitleBackLink Back { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>"Continuing" or "Ended", beside the genres.</summary>
    public TitleStatusChip? Status { get; init; }

    /// <summary>"2010– · 17 seasons · Discovery".</summary>
    public string? Meta { get; init; }

    /// <summary>Data at the start of the meta line, in mono: an episode's "S52 · E1".</summary>
    public string? MetaCode { get; init; }

    /// <summary>The end of the meta line for an episode not released yet: "Airs" and its release time.</summary>
    public TitleMetaRelease? MetaRelease { get; init; }

    /// <summary>IMDb's rating as OMDb gives it ("7.3"), when known.</summary>
    public string? ImdbRating { get; init; }

    /// <summary>The average of Recall users' ratings ("9.0"), when anyone has rated it.</summary>
    public string? RecallRating { get; init; }

    public int RecallRatingCount { get; init; }

    /// <summary>The user's own rating, behind the "Your rating" chip; null for a visitor.</summary>
    public RatingWidgetModel? Rating { get; init; }

    /// <summary>Only for a series in the user's library with something released; a movie has none.</summary>
    public TitleProgressModel? Progress { get; init; }

    /// <summary>An episode's place in its season, in place of <see cref="Progress"/>.</summary>
    public TitlePositionModel? Position { get; init; }

    public required TitleActionsModel Actions { get; init; }

    /// <summary>An episode's next episode, as a card under the action row (<c>_UpNextCard</c>).</summary>
    public UpNextModel? UpNext { get; init; }

    /// <summary>"Your rating 9", or "Rate" while unrated.</summary>
    public string RatingChipText => Rating?.CurrentValue is { } value
        ? $"Your rating {value.ToString(CultureInfo.InvariantCulture)}"
        : "Rate";
}

/// <summary>The way back at the top of the hero.</summary>
/// <param name="UseHistory">Go back in the browser's history when there is any, rather than to <paramref name="Page"/>.</param>
/// <param name="RouteId">The page's id, for a page that has one (an episode's series).</param>
public sealed record TitleBackLink(string Text, string Page, bool UseHistory, int? RouteId = null);

/// <summary>A link above the title to what it belongs to: an episode's series.</summary>
public sealed record TitleParentLink(string Text, string Page, int RouteId);

/// <summary>"Airs" and the release time, at the end of the meta line.</summary>
public sealed record TitleMetaRelease(string Prefix, ReleaseTimeModel Release);

/// <summary>
/// Where an episode sits in its season: "Episode 1 of 21 · Season 52", with
/// "N of M watched" and a bar for a signed-in user. The count is the season's
/// episodes; the watched figures are its released regular episodes, so a
/// season still airing reads "Episode 3 of 21" and "2 of 5 watched". A
/// special is "Special 3 of 4" alone: specials never count toward progress.
/// </summary>
/// <param name="WatchedCount">Null for a visitor and for a special: no watched line, no bar.</param>
public sealed record TitlePositionModel(string Label, int? WatchedCount = null, int ReleasedCount = 0, int? SeasonNumber = null)
{
    public bool HasBar => WatchedCount is not null && ReleasedCount > 0;

    /// <summary>Whole percent, rounded down, like the series' progress.</summary>
    public int Percent => ReleasedCount <= 0 || WatchedCount is not { } watched
        ? 0
        : (int)Math.Floor(100.0 * watched / ReleasedCount);

    /// <summary>"3 of 21 watched".</summary>
    public string? WatchedText => WatchedCount is { } watched
        ? $"{StatsFormat.Number(watched)} of {StatsFormat.Number(ReleasedCount)} watched"
        : null;
}

/// <summary>The watched state of an episode's control: a ring, a green check, or a dashed clock.</summary>
public enum UpNextMark
{
    /// <summary>A visitor: no control.</summary>
    None,
    Unwatched,
    Watched,
    /// <summary>May not be marked yet (<c>AirDate.MayBeMarked</c>): the dashed clock, not a button.</summary>
    NotAired
}

/// <summary>
/// Input for <c>_UpNextCard.cshtml</c>: the episode after this one in watch
/// order, under the action row. Its still by the shared rule
/// (<c>EpisodeArt</c>), "S52E02 · title" linking to it, when it airs, and for
/// a signed-in user the one-tap check (or the dashed clock).
/// </summary>
public sealed class UpNextModel
{
    public required int EpisodeId { get; init; }

    /// <summary>"S52E02".</summary>
    public required string Code { get; init; }

    public required string Title { get; init; }

    public string? ImageUrl { get; init; }

    /// <summary>"Aired Sun, Oct 4", or "Airs" before <see cref="Release"/>.</summary>
    public string? DateText { get; init; }

    /// <summary>For an episode not released yet: its release time after <see cref="DateText"/>.</summary>
    public ReleaseTimeModel? Release { get; init; }

    public UpNextMark Mark { get; init; }

    /// <summary>The page the check posts to (the episode being shown) and its handler.</summary>
    public required int RouteId { get; init; }

    public required string Handler { get; init; }

    /// <summary>Unwatched episodes before it; more than none and the check asks first (js/tvdb-series.js).</summary>
    public int PriorUnwatchedCount { get; init; }

    /// <summary>The check's name, as on Series Details' rows: "S52E02 Title watched".</summary>
    public string MarkLabel => $"{Code} {Title} watched";
}

/// <summary>
/// The title's status beside the genres: a series' ("Continuing", "Ended"), a
/// movie's ("Released", "Coming Fri, Mar 6"). <paramref name="Active"/>:
/// something is still to come (green).
/// </summary>
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
/// status box), then the square icon buttons: a "⋯" menu, a movie's
/// watchlist toggle, the heart and Share. A visitor gets the sign-in card
/// instead of the whole row.
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

    /// <summary>A movie's watchlist button (a square toggle); null where it does not apply.</summary>
    public TitleToggleSquare? Watchlist { get; init; }

    public LikeToggleModel? Like { get; init; }

    /// <summary>The name shared with the page's address (the canonical URL, read from the page).</summary>
    public required string ShareTitle { get; init; }
}

/// <summary>The one amber button of the action row: a POST to a page handler.</summary>
public sealed class TitleAction
{
    public required string Label { get; init; }

    /// <summary>A second, smaller line under the label ("Aired Fri, Nov 20 · 82 min", "2 h 50 min").</summary>
    public string? Detail { get; init; }

    public required string Handler { get; init; }

    public required int RouteId { get; init; }

    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Icon class, from <c>Icons</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// The episode the button marks, when marking it may first ask whether to
    /// mark the earlier unwatched episodes too: the form carries
    /// <c>data-prior-check</c>, and js/tvdb-series.js opens the "Catch up?"
    /// dialog. Without script the form marks just this one.
    /// </summary>
    public int? PriorCheckEpisodeId { get; init; }

    /// <summary>How many earlier episodes are unwatched (known to the page; with it, no request is needed to ask).</summary>
    public int? PriorUnwatchedCount { get; init; }
}

/// <summary>
/// A square toggle in the action row (a movie's watchlist): <c>aria-pressed</c>,
/// and a name that says the state ("Add to watchlist" / "On your watchlist").
/// Off, it posts to <see cref="Handler"/>; on, it opens the confirmation
/// <see cref="PressedModalId"/>, since turning it off removes something.
/// </summary>
public sealed class TitleToggleSquare
{
    public required bool IsPressed { get; init; }

    public required string Label { get; init; }

    public required string PressedLabel { get; init; }

    public required string Icon { get; init; }

    public required string PressedIcon { get; init; }

    public required string Handler { get; init; }

    public required int RouteId { get; init; }

    public required string PressedModalId { get; init; }

    /// <summary>The button's id, for a test or a script to find it.</summary>
    public string? Id { get; init; }

    public string CurrentLabel => IsPressed ? PressedLabel : Label;
}

/// <summary>A sentence before the primary button.</summary>
public sealed record TitleNotice(string Text, string Icon);

/// <summary>
/// The quiet box in place of the primary button: an icon, a line, and a
/// second line that may end in a release time ("S17E09 airs" then the
/// <c>&lt;time&gt;</c> of <paramref name="Release"/>).
/// </summary>
/// <remarks>
/// With <paramref name="ReleaseInLabel"/> the release time ends the first line
/// instead ("Airs Fri, Nov 27 · 8:00 PM ET": an episode not released yet).
/// </remarks>
public sealed record TitleStatusBox(string Text, string Icon, string? Detail = null, ReleaseTimeModel? Release = null, bool ReleaseInLabel = false);

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
