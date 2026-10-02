namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// How a watch row came to be, which says whether its <c>WatchedUtc</c> is a
/// date the title was plausibly watched on. Totals count every row; anything
/// laid out over time (the monthly chart on Stats) uses <see cref="Single"/>
/// and <see cref="Unknown"/> only, see <see cref="WatchSourceExtensions.IsDated"/>.
/// </summary>
public enum WatchSource
{
    /// <summary>
    /// A row from before the source was recorded (2026-10-02) that the
    /// migration's backfill did not recognise as a bulk mark or an import.
    /// </summary>
    Unknown = 0,

    /// <summary>Marked on its own: one episode or one movie, by one click.</summary>
    Single = 1,

    /// <summary>
    /// Marked together with others ("mark season watched", the earlier episodes
    /// of "mark this and earlier"): the time says when the user caught up, not
    /// when they watched.
    /// </summary>
    Bulk = 2,

    /// <summary>Written by the IMDb import.</summary>
    Import = 3
}

public static class WatchSourceExtensions
{
    /// <summary>Whether the row's <c>WatchedUtc</c> can stand in for when the title was watched.</summary>
    public static bool IsDated(this WatchSource source) => source is WatchSource.Single or WatchSource.Unknown;
}
