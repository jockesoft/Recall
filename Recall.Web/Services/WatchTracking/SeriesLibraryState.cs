using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.WatchTracking;

/// <summary>Where a tracked series stands for its user; the Library's four series sections.</summary>
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
    Finished,

    /// <summary>
    /// The user said they stopped watching it (<c>tracked_series.stopped_utc</c>),
    /// whatever its progress. It is still in the library, with its history.
    /// </summary>
    Stopped
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
    /// <para>
    /// <b>Stopped comes first.</b> A series the user stopped watching
    /// (<paramref name="stopped"/>, from <see cref="IsStopped"/>) is Stopped
    /// whatever its progress: never Watching (so it is in no continue-watching
    /// list and is never dormant, and no premiere brings it back), and never
    /// Finished (so Stats does not count it). Leave the argument out to ask
    /// where the series would stand if it were not stopped.
    /// </para>
    /// </summary>
    public static SeriesLibraryState Of(SeriesAggregate aggregate, SeriesWatchProgress progress, bool stopped = false)
    {
        if (stopped)
            return SeriesLibraryState.Stopped;

        if (!progress.IsUpToDate || !progress.HasStarted)
            return SeriesLibraryState.Watching;

        // TheTVDB's own status text: "Ended" means no more episodes are coming.
        var hasEnded = aggregate.Status?.Name?.Equals("Ended", StringComparison.OrdinalIgnoreCase) == true;

        return hasEnded ? SeriesLibraryState.Finished : SeriesLibraryState.UpToDate;
    }

    /// <summary>
    /// The one definition of "stopped watching": the library row has a stopped
    /// date. Everything that must leave a stopped series out asks this (or
    /// <see cref="Followed"/>), never the column itself.
    /// </summary>
    public static bool IsStopped(TrackedSeries tracked) => tracked.StoppedUtc is not null;

    /// <summary>
    /// The library without the series the user stopped watching: what the
    /// Dashboard and the weekly digest are built from.
    /// </summary>
    public static IReadOnlyList<TrackedSeries> Followed(IEnumerable<TrackedSeries> trackedSeries) =>
        trackedSeries.Where(s => !IsStopped(s)).ToList();

    /// <summary>
    /// Whether "Stop watching" is offered: for a series still being watched or
    /// up to date. Not for a finished one (there is nothing left to stop), and
    /// not for one already stopped.
    /// </summary>
    public static bool CanStop(SeriesLibraryState state) =>
        state is SeriesLibraryState.Watching or SeriesLibraryState.UpToDate;
}
