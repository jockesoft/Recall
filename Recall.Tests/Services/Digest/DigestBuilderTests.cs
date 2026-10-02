using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services.Digest;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services.Digest;

[TestFixture]
public sealed class DigestBuilderTests
{
    // A Friday.
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static EpisodeSummary Ep(int id, int season, int number, int daysFromToday, string? name = null) => new()
    {
        Id = id, SeasonNumber = season, EpisodeNumber = number, Name = name ?? $"S{season}E{number}", Aired = Today.AddDays(daysFromToday)
    };

    private static SeriesAggregate Series(int id, string name, params EpisodeSummary[] episodes) =>
        new() { TvdbId = id, Name = name, Episodes = episodes };

    private static DateTime DaysAgo(int days) => Today.AddDays(-days).ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

    private static DigestContent Build(
        SeriesAggregate[] tracked,
        int[]? watched = null,
        Dictionary<int, DateTime>? lastWatched = null,
        Dictionary<int, DateTime>? added = null,
        LibraryOptions? options = null) =>
        DigestBuilder.Build(tracked, (watched ?? []).ToHashSet(), lastWatched ?? [], added ?? [], Today, options ?? new LibraryOptions());

    // ---- ready to watch ----------------------------------------------------------

    [Test]
    public void ReadyToWatch_Should_ListUnwatchedEpisodesThatAiredInThePastWeek()
    {
        var content = Build(
            [Series(1, "Show", Ep(10, 1, 1, -30), Ep(11, 1, 2, -8), Ep(12, 1, 3, -7), Ep(13, 1, 4, -1), Ep(14, 1, 5, 0), Ep(15, 1, 6, 1))],
            watched: [10, 11, 12],
            lastWatched: new() { [1] = DaysAgo(2) });

        var line = content.ReadyToWatch.Items.Should().ContainSingle().Subject;
        line.SeriesName.Should().Be("Show");
        line.Code.Should().Be("S01 · E04–E05", "yesterday's and today's; tomorrow's has not aired and the watched ones are done");
        line.EpisodeCount.Should().Be(2);
        line.LinkEpisodeId.Should().Be(13);
        content.ReadyEpisodeCount.Should().Be(2);
    }

    [Test]
    public void ReadyToWatch_Should_IncludeTheDaySevenDaysAgo_AndNotTheDayBefore()
    {
        var content = Build(
            [Series(1, "Show", Ep(10, 1, 1, -30), Ep(11, 1, 2, -8), Ep(12, 1, 3, -7, "Exactly A Week")),],
            watched: [10],
            lastWatched: new() { [1] = DaysAgo(2) });

        var line = content.ReadyToWatch.Items.Should().ContainSingle().Subject;
        line.Code.Should().Be("S01 · E03");
        line.EpisodeName.Should().Be("Exactly A Week", "a single episode carries its name");
    }

    [Test]
    public void ReadyToWatch_Should_FollowTheContinueWatchingOrder()
    {
        var content = Build(
            [
                Series(1, "Alpha", Ep(10, 1, 1, -40), Ep(11, 1, 2, -1)),
                Series(2, "Beta", Ep(20, 1, 1, -40), Ep(21, 1, 2, -1)),
                Series(3, "Gamma, Never Started", Ep(30, 1, 1, -1))
            ],
            watched: [10, 20],
            lastWatched: new() { [1] = DaysAgo(20), [2] = DaysAgo(3) },
            added: new() { [3] = DaysAgo(5) });

        content.ReadyToWatch.Items.Select(l => l.SeriesId).Should().Equal(
            [2, 1], "most recently watched first; the never-started series is under New seasons instead (its S01E01 just aired)");
        content.NewSeasons.Items.Select(p => p.SeriesId).Should().Equal(3);
    }

    [Test]
    public void ATbaEpisodeName_Should_NotBePrinted()
    {
        var content = Build(
            [Series(1, "Show", Ep(10, 1, 1, -30), Ep(11, 1, 2, -1, "TBA"))],
            watched: [10],
            lastWatched: new() { [1] = DaysAgo(2) });

        content.ReadyToWatch.Items.Single().EpisodeName.Should().BeNull();
    }

    // ---- specials ----------------------------------------------------------------

    [Test]
    public void Specials_Should_NeverBeMentioned_InAnySection()
    {
        var content = Build(
            [Series(1, "Show", Ep(10, 1, 1, -30), Ep(5, 0, 1, -1, "Making Of"), Ep(6, 0, 2, 3, "Reunion"))],
            watched: [10],
            lastWatched: new() { [1] = DaysAgo(2) });

        content.IsEmpty.Should().BeTrue("a special that aired, a special that premieres and a special to come are all left out");
    }

    // ---- dormant series ----------------------------------------------------------

    [Test]
    public void ADormantSeries_Should_BeLeftOut_OfReadyToWatchAndComingUp()
    {
        var content = Build(
            [
                Series(1, "Abandoned Weekly Show", Ep(10, 3, 1, -200), Ep(11, 3, 30, -2), Ep(12, 3, 31, 5)),
                Series(2, "Active", Ep(20, 1, 1, -30), Ep(21, 1, 2, -2), Ep(22, 1, 3, 5))
            ],
            watched: [10, 20],
            lastWatched: new() { [1] = DaysAgo(180), [2] = DaysAgo(3) });

        content.ReadyToWatch.Items.Select(l => l.SeriesId).Should().Equal(2);
        content.ComingUp.Items.Select(l => l.SeriesId).Should().Equal(2);
        content.NewSeasons.Items.Should().BeEmpty("an ordinary episode is not a new season");
    }

    [Test]
    public void ADormantSeries_Should_AppearUnderNewSeasons_WhenASeasonPremiered()
    {
        var content = Build(
            [Series(1, "Back After A Year", Ep(10, 1, 1, -400), Ep(11, 1, 2, -393), Ep(12, 2, 1, -3))],
            watched: [10],
            lastWatched: new() { [1] = DaysAgo(390) });

        var premiere = content.NewSeasons.Items.Should().ContainSingle().Subject;
        premiere.SeriesName.Should().Be("Back After A Year");
        premiere.SeasonNumber.Should().Be(2);
        premiere.EpisodeId.Should().Be(12);
        content.ReadyToWatch.Items.Should().BeEmpty("a series named under New seasons is not repeated");
    }

    [Test]
    public void TheDormantRule_Should_UseTheLibrarySettings()
    {
        SeriesAggregate[] tracked = [Series(1, "Show", Ep(10, 1, 1, -200), Ep(11, 1, 2, -2))];

        Build(tracked, watched: [10], lastWatched: new() { [1] = DaysAgo(120) })
            .ReadyToWatch.Items.Should().BeEmpty("dormant after ninety days by default");

        Build(tracked, watched: [10], lastWatched: new() { [1] = DaysAgo(120) }, options: new LibraryOptions { DormantAfterDays = 0 })
            .ReadyToWatch.Items.Should().ContainSingle("with the dormant feature off, every series in progress is in the main list");
    }

    // ---- new seasons -------------------------------------------------------------

    [Test]
    public void NewSeasons_Should_NotMentionAPremiere_AlreadyWatched_OrOlderThanAWeek_OrStillToCome()
    {
        var content = Build(
            [
                Series(1, "Watched The Premiere", Ep(10, 1, 1, -100), Ep(11, 2, 1, -2), Ep(12, 2, 2, -1)),
                Series(2, "Premiered Eight Days Ago", Ep(20, 1, 1, -100), Ep(21, 2, 1, -8)),
                Series(3, "Premieres Next Week", Ep(30, 1, 1, -100), Ep(31, 2, 1, 4))
            ],
            watched: [10, 11, 20, 30],
            lastWatched: new() { [1] = DaysAgo(1), [2] = DaysAgo(50), [3] = DaysAgo(50) });

        content.NewSeasons.Items.Should().BeEmpty();
        content.ReadyToWatch.Items.Select(l => (l.SeriesId, l.Code)).Should().Equal(
            [(1, "S02 · E02")], "the premiere eight days ago is outside the week, so it is in neither section");
        content.ComingUp.Items.Select(l => (l.SeriesId, l.Code)).Should().Equal((3, "S02 · E01"));
    }

    // ---- coming up ---------------------------------------------------------------

    [Test]
    public void ComingUp_Should_ListTheNextSevenDays_SoonestFirst_ForSeriesTheUserIsCaughtUpOnToo()
    {
        var content = Build(
            [
                Series(1, "Caught Up", Ep(10, 1, 1, -30), Ep(11, 1, 2, 6), Ep(12, 1, 3, 7), Ep(13, 1, 4, 8)),
                Series(2, "Tomorrow", Ep(20, 1, 1, -30), Ep(21, 1, 2, 1)),
                Series(3, "Airs Today", Ep(30, 1, 1, -30), Ep(31, 1, 2, 0))
            ],
            watched: [10, 20, 30, 31]);

        content.ComingUp.Items.Select(l => (l.SeriesId, l.Code)).Should().Equal(
            [(2, "S01 · E02"), (1, "S01 · E02–E03")],
            "tomorrow before next week; day eight is outside the window; today is not coming up");
        content.ComingUp.Items[1].FirstAired.Should().Be(Today.AddDays(6));
        content.ComingEpisodeCount.Should().Be(3);
    }

    // ---- nothing to say, and too much to say --------------------------------------

    [Test]
    public void AnAnnouncedSeries_NeverStarted_Should_StayInComingUp_HoweverLongAgoItWasAdded()
    {
        // Under Watching in the Library and never dormant there: nothing has
        // aired, so there is nothing the user has left unwatched for a while.
        var content = Build(
            [Series(1, "Announced", Ep(10, 1, 1, 3))],
            added: new() { [1] = DaysAgo(200) });

        content.ComingUp.Items.Should().ContainSingle().Which.SeriesName.Should().Be("Announced");
    }

    [Test]
    public void ASeriesNeverStarted_WithEpisodesToWatch_Should_FollowTheDormantRule_FromTheDateAdded()
    {
        // Ended or not makes no difference here: the queue is "has a next episode".
        var content = Build(
            [
                Series(1, "Added This Week", Ep(10, 1, 1, -30), Ep(11, 1, 2, -1), Ep(12, 1, 3, 2)),
                Series(2, "Added Long Ago", Ep(20, 1, 1, -30), Ep(21, 1, 2, -1), Ep(22, 1, 3, 2))
            ],
            added: new() { [1] = DaysAgo(5), [2] = DaysAgo(200) });

        content.ReadyToWatch.Items.Select(l => l.SeriesName).Should().Equal("Added This Week");
        content.ComingUp.Items.Select(l => l.SeriesName).Should().Equal("Added This Week");
    }

    [Test]
    public void TheDigest_Should_BeEmpty_WhenThereIsNothingToSay()
    {
        Build([]).IsEmpty.Should().BeTrue();

        Build(
                [Series(1, "Quiet", Ep(10, 1, 1, -300), Ep(11, 1, 2, -290), Ep(12, 1, 3, 20))],
                watched: [10, 11])
            .IsEmpty.Should().BeTrue("nothing aired this week, nothing unwatched from it, nothing in the next seven days");
    }

    [Test]
    public void ASection_Should_ShowAtMostTenLines_AndCountTheRest()
    {
        var tracked = Enumerable.Range(1, 13)
            .Select(i => Series(i, $"Show {i:D2}", Ep(i * 100, 1, 1, -30), Ep(i * 100 + 1, 1, 2, 3)))
            .ToArray();

        var content = Build(tracked, watched: tracked.Select(s => s.TvdbId * 100).ToArray());

        content.ComingUp.Items.Should().HaveCount(DigestBuilder.MaxPerSection);
        content.ComingUp.MoreCount.Should().Be(3);
        content.ComingUp.TotalCount.Should().Be(13);
        content.IsEmpty.Should().BeFalse();
    }

    [Test]
    public void EpisodesOfTwoSeasons_Should_GetALineEach()
    {
        var content = Build(
            [Series(1, "Double Bill", Ep(10, 1, 1, -30), Ep(11, 1, 9, -3), Ep(12, 1, 10, -2), Ep(14, 2, 1, -10), Ep(13, 2, 2, -1))],
            watched: [10],
            lastWatched: new() { [1] = DaysAgo(4) });

        content.ReadyToWatch.Items.Select(l => l.Code).Should().Equal("S01 · E09–E10", "S02 · E02");
    }
}
