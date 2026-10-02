using AwesomeAssertions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public class WatchProgressCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 28);

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

        var progress = WatchProgressCalculator.Build(99, episodes, new HashSet<int> { 1 }, Today);

        progress.NextUnwatchedEpisode!.Id.Should().Be(2);
        progress.UnwatchedReleasedCount.Should().Be(2);
        progress.IsUpToDate.Should().BeFalse();
        progress.OrderedEpisodes.Select(e => e.Id).Should().ContainInOrder(1, 2, 3);
    }

    [Test]
    public void Build_Should_TreatEpisodeAiringToday_AsReleased()
    {
        var episodes = new[] { Ep(1, 1, 1, Today) };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Today);

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

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Today);

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

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 1, 2 }, Today);

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

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Today);

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

        WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Today)
            .NextUnwatchedEpisode!.Id.Should().Be(10, "a series nobody has started begins at S01E01, not at a making-of");

        WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10 }, Today)
            .NextUnwatchedEpisode!.Id.Should().Be(11);
    }

    [Test]
    public void Build_Should_OfferASpecial_OnlyWhenSpecialsAreAllThatRemain()
    {
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(21, 0, 2, Today.AddDays(-40)),
            Ep(10, 1, 1, Today.AddDays(-5)),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10, 20 }, Today);

        progress.NextUnwatchedEpisode!.Id.Should().Be(21);
        progress.IsUpToDate.Should().BeFalse("an unwatched special still counts as something to watch");
    }

    [Test]
    public void Build_Should_OfferASpecial_WhenTheOnlyRegularEpisodeLeftHasNotAired()
    {
        // The next regular episode is in the future: there is nothing regular
        // to watch now, so the unwatched special is what is left.
        var episodes = new[]
        {
            Ep(20, 0, 1, Today.AddDays(-50)),
            Ep(10, 1, 1, Today.AddDays(-5)),
            Ep(11, 1, 2, Today.AddDays(7)),
        };

        WatchProgressCalculator.Build(1, episodes, new HashSet<int> { 10 }, Today)
            .NextUnwatchedEpisode!.Id.Should().Be(20);
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
            WatchProgressCalculator.DefaultSeason(WatchProgressCalculator.Build(1, episodes, watched.ToHashSet(), Today));

        For().Should().Be(1, "nothing watched: start at season 1, not at the specials");
        For(10).Should().Be(2);
        For(10, 11).Should().Be(0, "only a special is left to watch");
        For(10, 11, 20).Should().Be(3, "caught up: the latest season, where the next episode will appear");
        WatchProgressCalculator.DefaultSeason(WatchProgressCalculator.Build(1, [], new HashSet<int>(), Today)).Should().BeNull();
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
