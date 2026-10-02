using Recall.Web.Infrastructure.Display;

namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// A mark just took a series from "something aired is unwatched" to "nothing
/// is": the moment the user caught up, or finished the series. Returned by the
/// marking methods of <see cref="IWatchProgressService"/>, which decide it by
/// comparing the series' state before and after the write
/// (<see cref="SeriesLibraryStateRule"/>); null on every other mark.
/// </summary>
/// <param name="Finished">The series has ended (the Library's Watched section); otherwise it is up to date and more will come.</param>
/// <param name="NextEpisode">For a series that is not finished: the next regular episode with a known, future air date, if there is one.</param>
/// <param name="UserHasRated">For a finished series: whether the user has already rated it. False invites them to.</param>
public sealed record SeriesCaughtUp(
    int SeriesTvdbId,
    string SeriesName,
    bool Finished,
    WatchableEpisode? NextEpisode = null,
    bool UserHasRated = true)
{
    /// <summary>Whether the toast should offer "Rate it".</summary>
    public bool OfferRating => Finished && !UserHasRated;

    /// <summary>
    /// What the toast says:
    /// "You've finished Chernobyl.",
    /// "You're up to date with Silo. Next episode S03E04 on Fri, Oct 9.", or
    /// "You're up to date with Silo. We'll let you know when a new episode airs."
    /// </summary>
    public string Sentence(DateOnly today)
    {
        if (Finished)
            return $"You've finished {SeriesName}.";

        return NextEpisode is { Aired: { } aired } next
            ? $"You're up to date with {SeriesName}. Next episode {next.SlateCode()} on {DisplayDate.Format(aired, today)}."
            : $"You're up to date with {SeriesName}. We'll let you know when a new episode airs.";
    }
}
