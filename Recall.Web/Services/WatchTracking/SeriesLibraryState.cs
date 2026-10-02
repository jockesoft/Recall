using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.WatchTracking;

/// <summary>Where a tracked series stands for its user; the Library's three series sections.</summary>
public enum SeriesLibraryState
{
    /// <summary>At least one aired regular episode is unwatched.</summary>
    Watching,

    /// <summary>Nothing aired is unwatched, and the series has not ended.</summary>
    UpToDate,

    /// <summary>Nothing aired is unwatched, and TheTVDB says the series has ended.</summary>
    Finished
}

public static class SeriesLibraryStateRule
{
    /// <summary>
    /// The one rule for which Library section a series belongs in, shared with
    /// the "series finished" figure on Stats so the two cannot disagree. It is
    /// about regular episodes only: unwatched specials never keep a series
    /// under Watching (see <see cref="WatchProgressCalculator"/>).
    /// </summary>
    public static SeriesLibraryState Of(SeriesAggregate aggregate, SeriesWatchProgress progress)
    {
        if (!progress.IsUpToDate)
            return SeriesLibraryState.Watching;

        // TheTVDB's own status text: "Ended" means no more episodes are coming.
        var hasEnded = aggregate.Status?.Name?.Equals("Ended", StringComparison.OrdinalIgnoreCase) == true;

        return hasEnded ? SeriesLibraryState.Finished : SeriesLibraryState.UpToDate;
    }
}
