namespace Recall.Web.Services.Discover;

/// <summary>The <c>Discover</c> section: what is good enough to suggest, how rows are filled, how often lists are fetched.</summary>
public sealed class DiscoverOptions
{
    public const string SectionName = "Discover";

    /// <summary>
    /// The lowest TheTVDB score a title needs. The score is an all-time count
    /// that moves slowly; most new titles have none (of the 2026 premieres of
    /// the last 60 days, 84% scored under 100), so this keeps out the obscure.
    /// </summary>
    public int MinScore { get; set; } = 100;

    /// <summary>The most titles one row shows.</summary>
    public int MaxPerRow { get; set; } = 20;

    /// <summary>A row with fewer titles than this (after what the user knows is left out) is not shown.</summary>
    public int MinPerRow { get; set; } = 6;

    /// <summary>"New series": premiered this many days ago at most.</summary>
    public int NewSeriesDays { get; set; } = 90;

    /// <summary>A genre row shows titles of the last this many years first (this year counts as one).</summary>
    public int RecentYears { get; set; } = 5;

    /// <summary>
    /// How many of the user's watched titles must have genres before the genre
    /// rows are theirs; with fewer, one "All-time favourites" row instead.
    /// </summary>
    public int MinGenreTitles { get; set; } = 5;

    /// <summary>
    /// One attempt at a filter list, its 400–500 KB body included, in seconds.
    /// Longer than other TheTVDB calls' 8 seconds, which a list sometimes needs
    /// (in the first real run one was cut off and fetched again). With the two
    /// retries a list can take up to three times this, plus the waits: fine for
    /// a background job, which never holds up a page.
    /// </summary>
    public int FilterAttemptTimeoutSeconds { get; set; } = 30;

    /// <summary>A list is fetched again once it is this old. The job checks hourly, so lists are fetched daily.</summary>
    public int RefreshAfterHours { get; set; } = 23;
}
