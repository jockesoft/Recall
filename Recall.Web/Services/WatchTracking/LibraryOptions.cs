namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// Bound from the <c>Library</c> configuration section: when a series the user
/// is in the middle of counts as one they "haven't watched in a while". The
/// rule that uses these is <see cref="ContinueWatchingOrder.Arrange"/>.
/// </summary>
public sealed class LibraryOptions
{
    public const string SectionName = "Library";

    /// <summary>
    /// Days without watch activity (or, for a series never started, days since
    /// it was added) after which a series in the queue is dormant. 0 or less
    /// turns the feature off: nothing is ever dormant.
    /// </summary>
    public int DormantAfterDays { get; set; } = 90;

    /// <summary>
    /// A dormant series comes back to the main list while the first episode of
    /// one of its regular seasons aired within this many days. 0 or less means
    /// a premiere never brings a series back.
    /// </summary>
    public int PremiereReturnDays { get; set; } = 14;
}
