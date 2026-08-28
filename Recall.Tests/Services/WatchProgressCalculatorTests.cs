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
    public void Build_Should_SortSpecialsBeforeSeasonOne()
    {
        var episodes = new[]
        {
            Ep(10, 1, 1, Today.AddDays(-5)),
            Ep(20, 0, 1, Today.AddDays(-5)),
        };

        var progress = WatchProgressCalculator.Build(1, episodes, new HashSet<int>(), Today);

        progress.OrderedEpisodes.Select(e => e.Id).Should().ContainInOrder(20, 10);
        progress.NextUnwatchedEpisode!.Id.Should().Be(20);
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
