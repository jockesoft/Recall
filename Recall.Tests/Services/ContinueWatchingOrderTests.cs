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

    // ---- grouping: "haven't watched in a while" --------------------------------

    private static readonly DateOnly Today = new(2026, 10, 1);
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

    private static DateTime DaysAgo(int days) => Today.AddDays(-days).ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

    private static (IReadOnlyList<int> Active, IReadOnlyList<int> Dormant) Arrange(
        IEnumerable<Show> shows,
        Dictionary<int, DateTime>? lastWatched = null,
        Dictionary<int, DateTime>? added = null,
        int[]? premieres = null,
        LibraryOptions? options = null)
    {
        var list = ContinueWatchingOrder.Arrange(
            shows, s => s.Id, s => s.Name, lastWatched ?? [], added ?? [],
            (premieres ?? []).ToHashSet(), Today, options ?? new LibraryOptions());

        return (list.Active.Select(s => s.Id).ToList(), list.Dormant.Select(s => s.Id).ToList());
    }

    [Test]
    public void ASeries_Should_BeDormant_OnceItsLastActivityIsOlderThanTheThreshold()
    {
        Show[] shows = [new(1, "Yesterday"), new(2, "Exactly Ninety Days"), new(3, "Ninety-One Days"), new(4, "A Year")];

        var (active, dormant) = Arrange(shows,
            lastWatched: new() { [1] = DaysAgo(1), [2] = DaysAgo(90), [3] = DaysAgo(91), [4] = DaysAgo(365) });

        active.Should().Equal([1, 2], "ninety days is still within the threshold");
        dormant.Should().Equal([3, 4], "the dormant group keeps the same order: most recently watched first");
    }

    [Test]
    public void ASeriesWithNothingToWatch_Should_NeverBeDormant_AndKeepItsPlaceInTheOrder()
    {
        Show[] shows = [new(1, "Added Last Week"), new(2, "Announced, Added Four Months Ago"), new(3, "Added A Year Ago")];

        var list = ContinueWatchingOrder.Arrange(
            shows, s => s.Id, s => s.Name, new Dictionary<int, DateTime>(),
            new Dictionary<int, DateTime> { [1] = DaysAgo(7), [2] = DaysAgo(120), [3] = DaysAgo(365) },
            new HashSet<int>(), Today, new LibraryOptions(),
            nothingToWatch: new HashSet<int> { 2 });

        list.Active.Select(s => s.Id).Should().Equal([1, 2], "not started, so after anything with activity, newest added first");
        list.Dormant.Select(s => s.Id).Should().Equal(3);
    }

    [Test]
    public void ASeriesNeverStarted_Should_BeDormant_OnceItWasAddedLongerAgoThanTheThreshold()
    {
        Show[] shows = [new(1, "Added Last Week"), new(2, "Added Four Months Ago"), new(3, "Added A Year Ago")];

        var (active, dormant) = Arrange(shows, added: new() { [1] = DaysAgo(7), [2] = DaysAgo(120), [3] = DaysAgo(365) });

        active.Should().Equal(1);
        dormant.Should().Equal([2, 3], "newest added first");
    }

    [Test]
    public void TheAddedDate_Should_NotKeepASeriesAwake_WhenItsLastActivityIsOld()
    {
        Show[] shows = [new(1, "Added Recently, Last Watched Long Ago"), new(2, "Added Long Ago, Watched Yesterday")];

        var (active, dormant) = Arrange(shows,
            lastWatched: new() { [1] = DaysAgo(200), [2] = DaysAgo(1) },
            added: new() { [1] = DaysAgo(10), [2] = DaysAgo(900) });

        active.Should().Equal([2], "for a series with activity only the activity counts");
        dormant.Should().Equal(1);
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void TheFeature_Should_BeOff_WhenTheThresholdIsZeroOrLess(int dormantAfterDays)
    {
        Show[] shows = [new(1, "Yesterday"), new(2, "Ten Years Ago"), new(3, "Never, Added Ten Years Ago")];

        var (active, dormant) = Arrange(shows,
            lastWatched: new() { [1] = DaysAgo(1), [2] = DaysAgo(3650) },
            added: new() { [3] = DaysAgo(3650) },
            options: new LibraryOptions { DormantAfterDays = dormantAfterDays });

        dormant.Should().BeEmpty();
        active.Should().Equal([1, 2, 3], "everything is in the main list, in the plain continue-watching order");
    }

    [Test]
    public void TheThreshold_Should_BeConfigurable()
    {
        Show[] shows = [new(1, "Eight Days"), new(2, "Six Days")];

        var (active, dormant) = Arrange(shows,
            lastWatched: new() { [1] = DaysAgo(8), [2] = DaysAgo(6) },
            options: new LibraryOptions { DormantAfterDays = 7 });

        active.Should().Equal(2);
        dormant.Should().Equal(1);
    }

    [Test]
    public void ASeriesWithNoKnownDate_Should_NotBeDormant()
    {
        Show[] shows = [new(1, "No Dates At All"), new(2, "Default Added Date")];

        var (active, dormant) = Arrange(shows, added: new() { [2] = default });

        active.Should().BeEquivalentTo([1, 2]);
        dormant.Should().BeEmpty("without a date there is nothing to call old");
    }

    [Test]
    public void ARecentSeasonPremiere_Should_BringADormantSeriesBack_AfterTheSeriesWithRealActivity()
    {
        Show[] shows =
        [
            new(1, "Watched Yesterday"),
            new(2, "Dormant, New Season"),
            new(3, "Not Started, Added Last Week"),
            new(4, "Dormant, Nothing New"),
            new(5, "Watched Last Month"),
            new(6, "Dormant And Never Started, New Season")
        ];

        var (active, dormant) = Arrange(shows,
            lastWatched: new() { [1] = DaysAgo(1), [2] = DaysAgo(300), [4] = DaysAgo(200), [5] = DaysAgo(30) },
            added: new() { [3] = DaysAgo(7), [6] = DaysAgo(400) },
            premieres: [2, 6]);

        active.Should().Equal(
            [1, 5, 2, 6, 3],
            "real activity first; then what a premiere brought back (in the usual order); then the series not started yet");
        dormant.Should().Equal(4);
    }

    [Test]
    public void APremiere_Should_ChangeNothing_ForASeriesThatIsNotDormant()
    {
        Show[] shows = [new(1, "Watched Yesterday"), new(2, "Watched Last Week, New Season")];

        var (active, _) = Arrange(shows, lastWatched: new() { [1] = DaysAgo(1), [2] = DaysAgo(7) }, premieres: [2]);

        active.Should().Equal([1, 2], "a series with real activity keeps its place by that activity");
    }

    // ---- what counts as a season premiere --------------------------------------

    // A US series at 20:00 Eastern: an episode aired on D is released at 00:00 UTC on D+1.
    private static WatchableEpisode Ep(int id, int? season, int? number, DateOnly? aired) =>
        new(id, season, number, aired, $"E{id}", EpisodeRelease.MomentUtc(aired, "20:00", "usa"));

    [Test]
    public void HasRecentPremiere_Should_BeTrue_WhenARegularSeasonsFirstEpisodeAiredWithinTheWindow()
    {
        WatchableEpisode[] episodes =
        [
            Ep(1, 1, 1, Today.AddDays(-400)),
            Ep(2, 1, 2, Today.AddDays(-393)),
            Ep(3, 2, 1, Today.AddDays(-14)),
            Ep(4, 2, 2, Today.AddDays(-7))
        ];

        // Released 13.5 days before now (Today 12:00 UTC).
        ContinueWatchingOrder.HasRecentPremiere(episodes, Now, days: 14).Should().BeTrue("inside a fourteen-day window");
        ContinueWatchingOrder.HasRecentPremiere(episodes, Now, days: 13).Should().BeFalse();
    }

    [Test]
    public void HasRecentPremiere_Should_BeFalse_ForAnOrdinaryEpisode()
    {
        // A weekly show mid-season: episodes keep airing, but no season started recently.
        WatchableEpisode[] episodes =
        [
            Ep(1, 22, 1, Today.AddDays(-120)),
            Ep(2, 22, 17, Today.AddDays(-8)),
            Ep(3, 22, 18, Today.AddDays(-1))
        ];

        ContinueWatchingOrder.HasRecentPremiere(episodes, Now, days: 14).Should().BeFalse(
            "an abandoned weekly show airs all the time and must be able to go dormant");
    }

    [Test]
    public void HasRecentPremiere_Should_NotCountSpecials()
    {
        WatchableEpisode[] episodes =
        [
            Ep(1, 0, 1, Today.AddDays(-2)),      // a special that "premiered" two days ago
            Ep(2, 1, 1, Today.AddDays(-500))
        ];

        ContinueWatchingOrder.HasRecentPremiere(episodes, Now, days: 14).Should().BeFalse();
    }

    [Test]
    public void HasRecentPremiere_Should_IgnoreThePremiereThatHasNotAiredYet_AndEpisodesWithoutADateOrNumber()
    {
        WatchableEpisode[] episodes =
        [
            Ep(1, 3, 1, Today.AddDays(3)),       // next week
            Ep(2, 4, 1, null),                   // announced, no date
            Ep(3, null, 1, Today.AddDays(-1)),   // no season
            Ep(4, 5, null, Today.AddDays(-1))    // no episode number
        ];

        ContinueWatchingOrder.HasRecentPremiere(episodes, Now, days: 14).Should().BeFalse();
        ContinueWatchingOrder.HasRecentPremiere([Ep(9, 3, 1, Today.AddDays(-1))], Now, days: 14)
            .Should().BeTrue("released at 00:00 UTC today");
        ContinueWatchingOrder.HasRecentPremiere([Ep(9, 3, 1, Today)], Now, days: 14)
            .Should().BeFalse("it airs tonight in the US: not released yet, so it brings nothing back");
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void HasRecentPremiere_Should_BeOff_WhenTheWindowIsZeroOrLess(int days)
    {
        ContinueWatchingOrder.HasRecentPremiere([Ep(1, 2, 1, Today)], Now, days).Should().BeFalse();
    }
}
