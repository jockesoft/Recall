using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.WatchTracking;

/// <summary>Where a tracked series stands for its user; the Library's three series sections.</summary>
public enum SeriesLibraryState
{
    /// <summary>
    /// At least one aired regular episode is unwatched, or the series has not
    /// been started at all (no regular episode watched).
    /// </summary>
    Watching,

    /// <summary>Started, nothing aired is unwatched, and the series has not ended.</summary>
    UpToDate,

    /// <summary>Started, nothing aired is unwatched, and TheTVDB says the series has ended.</summary>
    Finished
}

public static class SeriesLibraryStateRule
{
    /// <summary>
    /// The one rule for which Library section a series belongs in, shared with
    /// the "series finished" figure on Stats so the two cannot disagree.
    /// <list type="bullet">
    /// <item>It is about regular episodes only: unwatched specials never keep
    /// a series under Watching, and watched specials never make it started
    /// (see <see cref="WatchProgressCalculator"/>).</item>
    /// <item>A series is Finished or UpToDate only when the user has watched
    /// at least one regular episode <i>and</i> every aired one. A series never
    /// started is Watching whether it has ended or not, including one with
    /// nothing aired yet: "nothing left to watch" is not "watched it".</item>
    /// </list>
    /// </summary>
    public static SeriesLibraryState Of(SeriesAggregate aggregate, SeriesWatchProgress progress)
    {
        if (!progress.IsUpToDate || !progress.HasStarted)
            return SeriesLibraryState.Watching;

        // TheTVDB's own status text: "Ended" means no more episodes are coming.
        var hasEnded = aggregate.Status?.Name?.Equals("Ended", StringComparison.OrdinalIgnoreCase) == true;

        return hasEnded ? SeriesLibraryState.Finished : SeriesLibraryState.UpToDate;
    }
}
