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
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

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
        DigestBuilder.Build(tracked, (watched ?? []).ToHashSet(), lastWatched ?? [], added ?? [], Now, options ?? new LibraryOptions());

    // ---- ready to watch ----------------------------------------------------------

    [Test]
    public void ReadyToWatch_Should_ListUnwatchedEpisodesThatAiredInThePastWeek()
    {
        var content = Build(
            [Series(1, "Show", Ep(10, 1, 1, -30), Ep(11, 1, 2, -8), Ep(12, 1, 3, -7), Ep(13, 1, 4, -1), Ep(14, 1, 5, 0), Ep(15, 1, 6, 1))],
            watched: [10, 11, 12],
            lastWatched: new() { [1] = DaysAgo(2) });

        // These series have no air time or country, so an episode is released at
        // noon UTC the day after its air date (EpisodeRelease's fallback); now is
        // noon on Today. Yesterday's is released (at noon today); today's is not.
        var line = content.ReadyToWatch.Items.Should().ContainSingle().Subject;
        line.SeriesName.Should().Be("Show");
        line.Code.Should().Be("S01 · E04", "yesterday's; today's is not released yet and the watched ones are done");
        line.EpisodeCount.Should().Be(1);
        line.LinkEpisodeId.Should().Be(13);
        content.ReadyEpisodeCount.Should().Be(1);
        content.ComingUp.Items.Should().ContainSingle().Which.Code.Should().Be("S01 · E05–E06", "today's and tomorrow's are coming up");
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

        // Released at noon UTC the day after the air date (no air time or country), now is noon today:
        // the day-six episode is released exactly seven days from now (inside), the day-seven one after.
        content.ComingUp.Items.Select(l => (l.SeriesId, l.Code)).Should().Equal(
            [(2, "S01 · E02"), (1, "S01 · E02")],
            "tomorrow's before next week's; the rest is outside the window; today's is already marked watched");
        content.ComingUp.Items[1].FirstAired.Should().Be(Today.AddDays(6));
        content.ComingEpisodeCount.Should().Be(2);
    }

    // ---- Gold Rush S17E01: aired Friday 2026-10-02 at 20:00 Eastern, released 00:00 UTC Saturday ----

    private static SeriesAggregate GoldRush() => new()
    {
        TvdbId = 208111, Name = "Gold Rush", AirsTime = "20:00", OriginalCountry = "usa",
        Episodes =
        [
            new EpisodeSummary { Id = 1, SeasonNumber = 16, EpisodeNumber = 9, Name = "Finale", Aired = new DateOnly(2026, 5, 1) },
            new EpisodeSummary { Id = 11961330, SeasonNumber = 17, EpisodeNumber = 1, Name = "The Most Gold Wins", Aired = new DateOnly(2026, 10, 2) }
        ]
    };

    [Test]
    public void GoldRushS17E01_Should_BeComingUp_InFridayOctoberSecondsDigest()
    {
        // The digest goes out at 15:00 UTC: five hours before it airs in the US.
        var content = DigestBuilder.Build(
            [GoldRush()], new HashSet<int> { 1 }, new Dictionary<int, DateTime> { [208111] = DaysAgo(3) }, new Dictionary<int, DateTime>(),
            new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc), new LibraryOptions());

        content.ReadyToWatch.Items.Should().BeEmpty();
        content.NewSeasons.Items.Should().BeEmpty("it has not premiered yet");
        var line = content.ComingUp.Items.Should().ContainSingle().Subject;
        line.Code.Should().Be("S17 · E01");
        line.FirstAired.Should().Be(new DateOnly(2026, 10, 2), "the date shown stays TheTVDB's");
    }

    [Test]
    public void GoldRushS17E01_Should_BeANewSeason_InFridayOctoberNinthsDigest()
    {
        // A week later it was released (Saturday 00:00 UTC) and is unwatched: as the
        // first episode of a season it is listed under New seasons, which takes
        // the series out of Ready to watch, and it is no longer coming up.
        var content = DigestBuilder.Build(
            [GoldRush()], new HashSet<int> { 1 }, new Dictionary<int, DateTime> { [208111] = DaysAgo(3) }, new Dictionary<int, DateTime>(),
            new DateTime(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc), new LibraryOptions());

        var premiere = content.NewSeasons.Items.Should().ContainSingle().Subject;
        premiere.SeriesName.Should().Be("Gold Rush");
        premiere.SeasonNumber.Should().Be(17);
        content.ComingUp.Items.Should().BeEmpty();
    }

    [Test]
    public void GoldRushS17E02_Should_BeReadyToWatch_TheFridayAfterItAired()
    {
        // S17E02 aired Friday Oct 9 (released Saturday Oct 10 00:00 UTC): ready in the Oct 16 digest.
        var series = GoldRush() with
        {
            Episodes = [.. GoldRush().Episodes,
                new EpisodeSummary { Id = 11997727, SeasonNumber = 17, EpisodeNumber = 2, Name = "Two", Aired = new DateOnly(2026, 10, 9) }]
        };

        var content = DigestBuilder.Build(
            [series], new HashSet<int> { 1, 11961330 }, new Dictionary<int, DateTime> { [208111] = new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc) }, new Dictionary<int, DateTime>(),
            new DateTime(2026, 10, 16, 15, 0, 0, DateTimeKind.Utc), new LibraryOptions());

        content.ReadyToWatch.Items.Should().ContainSingle().Which.Code.Should().Be("S17 · E02");
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
