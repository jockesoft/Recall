using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

/// <summary>
/// The one rule behind the Library's Watching / Up to date / Watched sections
/// and the "series finished" figure on Stats.
/// </summary>
[TestFixture]
public sealed class SeriesLibraryStateRuleTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

    private static EpisodeSummary Ep(int id, int season, int number, int daysFromToday) => new()
    {
        Id = id, SeasonNumber = season, EpisodeNumber = number, Name = $"S{season}E{number}", Aired = Today.AddDays(daysFromToday)
    };

    private static SeriesLibraryState StateOf(string status, EpisodeSummary[] episodes, params int[] watched)
    {
        var aggregate = new SeriesAggregate
        {
            TvdbId = 1, Name = "Show", Status = new SeriesStatus { Name = status }, Episodes = episodes
        };
        var progress = WatchProgressCalculator.Build(1, Watchable(episodes), watched.ToHashSet(), Now);

        return SeriesLibraryStateRule.Of(aggregate, progress);
    }

    private static List<WatchableEpisode> Watchable(IEnumerable<EpisodeSummary> episodes) =>
        episodes.Select(e => new WatchableEpisode(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Aired, e.Name)).ToList();

    private static readonly EpisodeSummary[] TwoAiredAndASpecial = [Ep(5, 0, 1, -300), Ep(10, 1, 1, -400), Ep(11, 1, 2, -390)];

    [Test]
    public void AnEndedSeries_NeverStarted_Should_BeWatching_NotWatched()
    {
        StateOf("Ended", TwoAiredAndASpecial).Should().Be(SeriesLibraryState.Watching);
    }

    [Test]
    public void AnEndedSeries_FullyWatched_Should_BeFinished()
    {
        StateOf("Ended", TwoAiredAndASpecial, 10, 11).Should().Be(SeriesLibraryState.Finished,
            "every aired regular episode is watched; the unwatched special does not matter");
    }

    [Test]
    public void AnEndedSeries_WithOnlySpecialsWatched_Should_BeWatching()
    {
        StateOf("Ended", TwoAiredAndASpecial, 5).Should().Be(SeriesLibraryState.Watching,
            "a special does not start a series");
    }

    [Test]
    public void AnEndedSeries_PartlyWatched_Should_BeWatching()
    {
        StateOf("Ended", TwoAiredAndASpecial, 10).Should().Be(SeriesLibraryState.Watching);
    }

    [TestCase("Ended")]
    [TestCase("Continuing")]
    [TestCase("Upcoming")]
    public void ASeriesWithNothingAiredYet_NeverStarted_Should_BeWatching_WhateverItsStatus(string status)
    {
        // "Nothing left to watch" is not "watched it": no regular episode has
        // aired (an announced series), or the series has only specials.
        StateOf(status, [Ep(10, 1, 1, 30)]).Should().Be(SeriesLibraryState.Watching);
        StateOf(status, [Ep(5, 0, 1, -300)]).Should().Be(SeriesLibraryState.Watching);
        StateOf(status, [Ep(5, 0, 1, -300)], 5).Should().Be(SeriesLibraryState.Watching, "only its special is watched");
        StateOf(status, []).Should().Be(SeriesLibraryState.Watching);
    }

    [Test]
    public void AContinuingSeries_WithEverythingAiredWatched_Should_BeUpToDate()
    {
        StateOf("Continuing", [Ep(10, 1, 1, -30), Ep(11, 1, 2, 5)], 10).Should().Be(SeriesLibraryState.UpToDate);
    }

    [Test]
    public void EpisodesWithoutAnAirDate_ThatWereWatched_Should_CountAsStarted()
    {
        // An undated episode can be marked watched but never counts as aired;
        // a series made of them that the user has watched is still finished.
        EpisodeSummary[] undated =
        [
            new() { Id = 10, SeasonNumber = 1, EpisodeNumber = 1, Name = "One" },
            new() { Id = 11, SeasonNumber = 1, EpisodeNumber = 2, Name = "Two" }
        ];

        StateOf("Ended", undated, 10, 11).Should().Be(SeriesLibraryState.Finished);
        StateOf("Ended", undated).Should().Be(SeriesLibraryState.Watching);
    }

    [Test]
    public void HasStarted_Should_BeAboutRegularEpisodesOnly()
    {
        var episodes = Watchable(TwoAiredAndASpecial);

        WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Now).HasStarted.Should().BeFalse();
        WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 5 }, Now).HasStarted.Should().BeFalse();
        WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10 }, Now).HasStarted.Should().BeTrue();
    }
}
