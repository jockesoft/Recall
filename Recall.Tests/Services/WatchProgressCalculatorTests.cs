using AwesomeAssertions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public class WatchProgressCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 28);
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

    private static WatchableEpisode Ep(int id, int season, int number, DateOnly? aired) =>
        new(id, season, number, aired, $"S{season}E{number}");

    [Test]
    public void Build_Should_PickEarliestUnwatchedReleasedEpisode_AcrossSeasons()
    {
        var episodes = new[]
        {
            Ep(3, 2, 1, Today.AddDays(-2)),
            Ep(1, 1, 1, Today.AddDays(-40)),
            Ep(2, 1, 2, Today.AddDays(-30)),
        };

        var progress = WatchProgressCalculator.Build(99, episodes, new HashSet<int> { 1 }, Now);

        progress.NextUnwatchedEpisode!.Id.Should().Be(2);
        progress.UnwatchedReleasedCount.Should().Be(2);
        progress.IsUpToDate.Should().BeFalse();
        progress.OrderedEpisodes.Select(e => e.Id).Should().ContainInOrder(1, 2, 3);
    }

    [Test]
    public void Build_Should_CountAnEpisode_FromItsReleaseMoment_NotFromItsAirDate()
    {
        // Gold Rush S17E01: aired 2026-10-02 at 20:00 Eastern, released 2026-10-03 00:00 UTC.
        var s17e01 = new WatchableEpisode(11961330, 17, 1, new DateOnly(2026, 10, 2), "The Most Gold Wins",
            EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), "20:00", "usa"));
        var earlier = new WatchableEpisode(1, 16, 9, new DateOnly(2026, 5, 1), "Finale",
            EpisodeRelease.MomentUtc(new DateOnly(2026, 5, 1), "20:00", "usa"));

        var evening = WatchProgressCalculator.Build(1, [earlier, s17e01], new HashSet<int> { 1 },
            new DateTime(2026, 10, 2, 22, 0, 0, DateTimeKind.Utc));
        evening.ReleasedCount.Should().Be(1, "on Oct 2 at 22:00 UTC it has not aired in the US yet");
        evening.NextUnwatchedEpisode.Should().BeNull();
        evening.IsUpToDate.Should().BeTrue();

        var midnight = WatchProgressCalculator.Build(1, [earlier, s17e01], new HashSet<int> { 1 },
            new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        midnight.ReleasedCount.Should().Be(2);
        midnight.NextUnwatchedEpisode!.Id.Should().Be(11961330);
    }

    [Test]
    public void Build_Should_UseTheNoCountryFallback_ForAnEpisodeWithoutAReleaseMoment()
    {
        // Built without the series' air time and country: released at noon UTC the day after.
        var episodes = new[] { Ep(1, 1, 1, Today.AddDays(-1)), Ep(2, 1, 2, Today) };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Now);

        progress.ReleasedCount.Should().Be(1, "yesterday's is released at noon today; today's at noon tomorrow");
        progress.NextUnwatchedEpisode!.Id.Should().Be(1);
    }

    [Test]
    public void Build_Should_IgnoreFutureDatedAndUndatedEpisodes()
    {
        var episodes = new[]
        {
            Ep(1, 1, 1, Today.AddDays(3)),
            Ep(2, 1, 2, null),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Now);

        progress.NextUnwatchedEpisode.Should().BeNull();
        progress.UnwatchedReleasedCount.Should().Be(0);
        progress.IsUpToDate.Should().BeTrue();
    }

    [Test]
    public void Build_Should_ReportUpToDate_WhenAllReleasedEpisodesWatched()
    {
        var episodes = new[]
        {
            Ep(1, 1, 1, Today.AddDays(-10)),
            Ep(2, 1, 2, Today.AddDays(-3)),
            Ep(3, 1, 3, Today.AddDays(5)),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 1, 2 }, Now);

        progress.IsUpToDate.Should().BeTrue();
        progress.UnwatchedReleasedCount.Should().Be(0);
    }

    [Test]
    public void Build_Should_PutSpecialsAfterTheNumberedSeasons()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(10, 1, 1, Today.AddDays(-5)),
            Ep(30, 2, 1, Today.AddDays(-1)),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Now);

        progress.OrderedEpisodes.Select(e => e.Id).Should().Equal(10, 30, 20);
    }

    [Test]
    public void Build_Should_SkipSpecials_WhenPickingTheNextEpisode()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(21, 0, 2, Today.AddDays(-40)),
            Ep(10, 1, 1, Today.AddDays(-5)),
            Ep(11, 1, 2, Today.AddDays(-4)),
        };

        WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Now)
            .NextUnwatchedEpisode!.Id.Should().Be(10, "a series nobody has started begins at S01E01, not at a making-of");

        WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10 }, Now)
            .NextUnwatchedEpisode!.Id.Should().Be(11);
    }

    [Test]
    public void Build_Should_BeUpToDate_WhenOnlySpecialsAreUnwatched()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(21, 0, 2, Today.AddDays(-40)),
            Ep(10, 1, 1, Today.AddDays(-5)),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10 }, Now);

        progress.NextUnwatchedEpisode.Should().BeNull("a special is never the next episode");
        progress.IsUpToDate.Should().BeTrue("a making-of must not keep a finished series in the queue");
        progress.UnwatchedReleasedCount.Should().Be(0);
    }

    [Test]
    public void Build_Should_NeverOfferASpecial_EvenWhenTheNextRegularEpisodeHasNotAired()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(10, 1, 1, Today.AddDays(-5)),
            Ep(11, 1, 2, Today.AddDays(7)),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10 }, Now);

        progress.NextUnwatchedEpisode.Should().BeNull();
        progress.IsUpToDate.Should().BeTrue();
    }

    [Test]
    public void Build_Should_LeaveSpecialsOutOfTheCounts()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(21, 0, 2, Today.AddDays(-40)),
            Ep(10, 1, 1, Today.AddDays(-5)),
            Ep(11, 1, 2, Today.AddDays(-4)),
        };

        // One regular episode and one special watched.
        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10, 20 }, Now);

        progress.ReleasedCount.Should().Be(2);
        progress.WatchedReleasedCount.Should().Be(1, "a watched special is not progress through the series");
        progress.UnwatchedReleasedCount.Should().Be(1);
        progress.OrderedEpisodes.Should().HaveCount(4, "the specials are still listed");
        progress.WatchedEpisodeIds.Should().Contain(20, "and still shown as watched");
    }

    [Test]
    public void Build_Should_BeUpToDate_ForASeriesWithOnlySpecials()
    {
        var episodes = new[] { Ep(20, 0, 1, Today.AddDays(-50)) };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Now);

        progress.IsUpToDate.Should().BeTrue();
        progress.ReleasedCount.Should().Be(0);
        WatchProgressCalculator.DefaultSeason(progress).Should().Be(0, "it is the only season there is");
    }

    [Test]
    public void Build_Should_ReportProgressThroughTheSeasonOfTheNextEpisode()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(10, 1, 1, Today.AddDays(-30)),
            Ep(11, 1, 2, Today.AddDays(-29)),
            Ep(12, 2, 1, Today.AddDays(-9)),
            Ep(13, 2, 2, Today.AddDays(-8)),
            Ep(14, 2, 3, Today.AddDays(-7)),
            Ep(15, 2, 4, Today.AddDays(7)),
        };

        var midSeason = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10, 11, 12, 20 }, Now);
        var caughtUp = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10, 11, 12, 13, 14 }, Now);

        midSeason.CurrentSeason.Should().Be(new SeasonWatchProgress(2, WatchedCount: 1, ReleasedCount: 3),
            "season 2 has three aired episodes; the one still to air is not counted");
        midSeason.CurrentSeason!.Label.Should().Be("1 of 3 · S02");
        caughtUp.CurrentSeason.Should().BeNull("there is no next episode to be in a season");
    }

    [Test]
    public void DefaultSeason_Should_FollowTheNextEpisode_ThenTheLatestSeason()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(10, 1, 1, Today.AddDays(-9)),
            Ep(11, 2, 1, Today.AddDays(-5)),
            Ep(12, 3, 1, Today.AddDays(7)),
        };

        int? For(params int[] watched) =>
            WatchProgressCalculator.DefaultSeason(WatchProgressCalculator.Build(1, episodes, watched.ToHashSet(), Now));

        For().Should().Be(1, "nothing watched: start at season 1, not at the specials");
        For(10).Should().Be(2);
        For(10, 11).Should().Be(3, "caught up, with a special unwatched: the latest season, never the specials");
        For(10, 11, 20).Should().Be(3, "caught up: the latest season, where the next episode will appear");
        WatchProgressCalculator.DefaultSeason(WatchProgressCalculator.Build(1, [], new HashSet<int>(), Now)).Should().BeNull();
    }

    [Test]
    public void CountPriorUnwatched_Should_NotCountSpecials_ForARegularEpisode()
    {
        var ordered = WatchProgressCalculator.Order(new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(21, 0, 2, Today.AddDays(-40)),
            Ep(1, 1, 1, Today.AddDays(-5)),
            Ep(2, 1, 2, Today.AddDays(-4)),
        });

        WatchProgressCalculator.CountPriorUnwatched(ordered, new HashSet<int>(), episodeTvdbId: 2).Should().Be(1);
        WatchProgressCalculator.IdsThrough(ordered, episodeTvdbId: 2).Should().Equal(1, 2);
    }

    [Test]
    public void CountPriorUnwatched_Should_CountOnlyEarlierSpecials_ForASpecial()
    {
        var ordered = WatchProgressCalculator.Order(new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(21, 0, 2, Today.AddDays(-40)),
            Ep(1, 1, 1, Today.AddDays(-5)),
            Ep(2, 1, 2, Today.AddDays(-4)),
        });

        WatchProgressCalculator.CountPriorUnwatched(ordered, new HashSet<int>(), episodeTvdbId: 21)
            .Should().Be(1, "marking a special must not offer to mark the whole series");
        WatchProgressCalculator.IdsThrough(ordered, episodeTvdbId: 21).Should().Equal(20, 21);
    }

    [Test]
    public void CountPriorUnwatched_Should_CountOnlyUnwatchedEpisodesBeforeTarget()
    {
        var ordered = WatchProgressCalculator.Order(new[]
        {
            Ep(1, 1, 1, Today.AddDays(-5)),
            Ep(2, 1, 2, Today.AddDays(-4)),
            Ep(3, 1, 3, Today.AddDays(-3)),
        });

        WatchProgressCalculator.CountPriorUnwatched(ordered, new HashSet<int> { 1 }, episodeTvdbId: 3)
            .Should().Be(1);
    }

    [Test]
    public void CountPriorUnwatched_Should_ReturnZero_ForFirstOrUnknownEpisode()
    {
        var ordered = WatchProgressCalculator.Order(new[] { Ep(1, 1, 1, Today), Ep(2, 1, 2, Today) });

        WatchProgressCalculator.CountPriorUnwatched(ordered, new HashSet<int>(), episodeTvdbId: 1).Should().Be(0);
        WatchProgressCalculator.CountPriorUnwatched(ordered, new HashSet<int>(), episodeTvdbId: 999).Should().Be(0);
    }

    [Test]
    public void IdsThrough_Should_ReturnEpisodesUpToAndIncludingTarget()
    {
        var ordered = WatchProgressCalculator.Order(new[]
        {
            Ep(1, 1, 1, Today), Ep(2, 1, 2, Today), Ep(3, 1, 3, Today),
        });

        WatchProgressCalculator.IdsThrough(ordered, episodeTvdbId: 2).Should().Equal(1, 2);
    }

    [Test]
    public void IdsThrough_Should_ReturnJustTheId_WhenNotInList()
    {
        var ordered = WatchProgressCalculator.Order(new[] { Ep(1, 1, 1, Today) });

        WatchProgressCalculator.IdsThrough(ordered, episodeTvdbId: 42).Should().Equal(42);
    }
}
