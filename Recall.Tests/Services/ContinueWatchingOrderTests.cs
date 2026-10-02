using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public sealed class ContinueWatchingOrderTests
{
    private sealed record Show(int Id, string Name);

    private static DateTime Day(int day) => new(2026, 9, day, 20, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<int> Order(
        IEnumerable<Show> shows,
        Dictionary<int, DateTime>? lastWatched = null,
        Dictionary<int, DateTime>? added = null) =>
        ContinueWatchingOrder
            .Order(shows, s => s.Id, s => s.Name, lastWatched ?? [], added ?? [])
            .Select(s => s.Id)
            .ToList();

    [Test]
    public void SeriesWithActivity_Should_ComeFirst_MostRecentlyWatchedFirst()
    {
        Show[] shows = [new(1, "Alpha"), new(2, "Beta"), new(3, "Gamma")];

        var order = Order(shows, lastWatched: new() { [1] = Day(3), [2] = Day(20), [3] = Day(11) });

        order.Should().Equal(2, 3, 1);
    }

    [Test]
    public void SeriesWithNothingWatched_Should_ComeAfterEverySeriesWithActivity()
    {
        Show[] shows = [new(1, "Added Today, Never Watched"), new(2, "Watched Long Ago")];

        var order = Order(shows,
            lastWatched: new() { [2] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            added: new() { [1] = Day(30), [2] = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        order.Should().Equal([2, 1], "any activity, however old, puts a series ahead of one never started");
    }

    [Test]
    public void SeriesWithNothingWatched_Should_BeOrderedByWhenTheyWereAdded_NewestFirst()
    {
        Show[] shows = [new(1, "Alpha"), new(2, "Beta"), new(3, "Gamma")];

        var order = Order(shows, added: new() { [1] = Day(1), [2] = Day(25), [3] = Day(12) });

        order.Should().Equal(2, 3, 1);
    }

    [Test]
    public void TheAddedDate_Should_NotMatter_ForASeriesWithActivity()
    {
        Show[] shows = [new(1, "Added Recently, Watched Earlier"), new(2, "Added Long Ago, Watched Yesterday")];

        var order = Order(shows,
            lastWatched: new() { [1] = Day(5), [2] = Day(29) },
            added: new() { [1] = Day(28), [2] = Day(1) });

        order.Should().Equal(2, 1);
    }

    [Test]
    public void Ties_Should_BeBrokenByName_ThenById_SoTheOrderIsStable()
    {
        Show[] shows = [new(4, "zebra"), new(3, "Alpha"), new(2, "beta"), new(1, "Alpha")];
        var sameMoment = new Dictionary<int, DateTime> { [1] = Day(9), [2] = Day(9), [3] = Day(9), [4] = Day(9) };

        Order(shows, lastWatched: sameMoment).Should().Equal([1, 3, 2, 4], "watched at the same moment: by name ignoring case, then by id");
        Order(shows.Reverse(), lastWatched: sameMoment).Should().Equal([1, 3, 2, 4], "whatever order they came in");
        Order(shows, added: sameMoment).Should().Equal([1, 3, 2, 4], "the same for series added at the same moment");
        Order(shows).Should().Equal([1, 3, 2, 4], "and for series with no dates at all");
    }

    [Test]
    public void WatchingASpecial_Should_CountAsActivity()
    {
        // The repository's map is keyed by series and built from every episode
        // watch, specials included, so a special watched yesterday is simply the
        // series' latest activity. The order has no notion of seasons.
        Show[] shows = [new(1, "Regular Episode Last Week"), new(2, "Special Yesterday")];

        var order = Order(shows, lastWatched: new() { [1] = Day(22), [2] = Day(29) });

        order.Should().Equal(2, 1);
    }

    [Test]
    public void AddedUtc_Should_MapEachTrackedSeriesToWhenItWasAdded()
    {
        TrackedSeries[] tracked =
        [
            new() { TvdbId = 10, Name = "A", CreatedUtc = Day(2) },
            new() { TvdbId = 20, Name = "B", CreatedUtc = Day(7) }
        ];

        ContinueWatchingOrder.AddedUtc(tracked).Should().Equal(new Dictionary<int, DateTime> { [10] = Day(2), [20] = Day(7) });
    }
}
