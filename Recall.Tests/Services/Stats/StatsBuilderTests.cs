using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Services.Stats;

[TestFixture]
public sealed class StatsBuilderTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
    private static readonly StatsWindow Window = StatsWindow.LastTwelveMonths(Today);

    private readonly List<EpisodeWatchRecord> _episodeWatches = [];
    private readonly List<MovieWatch> _movieWatches = [];
    private readonly Dictionary<int, SeriesAggregate> _series = [];
    private readonly Dictionary<int, MovieAggregate> _movies = [];
    private readonly HashSet<int> _tracked = [];
    private readonly Dictionary<int, int> _ratings = [];

    [SetUp]
    public void SetUp()
    {
        _episodeWatches.Clear();
        _movieWatches.Clear();
        _series.Clear();
        _movies.Clear();
        _tracked.Clear();
        _ratings.Clear();
    }

    private UserStats Build() => StatsBuilder.Build(
        new StatsInput(_episodeWatches, _movieWatches, _series, _movies, _tracked, _ratings), Now, Window);

    private static DateTime Utc(int year, int month, int day, int hour = 20) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static EpisodeSummary Ep(int id, int season, int number, int? runtime = null, string aired = "2020-01-01", bool isMovie = false) => new()
    {
        Id = id,
        SeasonNumber = season,
        EpisodeNumber = number,
        Name = $"Episode {id}",
        Aired = DateOnly.Parse(aired),
        RuntimeMinutes = runtime,
        IsMovie = isMovie
    };

    private void Series(
        int id, string name, int? averageRuntime, string[]? genres = null, string status = "Continuing", params EpisodeSummary[] episodes) =>
        _series[id] = new SeriesAggregate
        {
            TvdbId = id,
            Name = name,
            AverageRuntimeMinutes = averageRuntime,
            Genres = genres ?? [],
            Status = new SeriesStatus { Name = status },
            Episodes = episodes
        };

    private void Movie(int id, string name, int? runtime, params string[] genres) =>
        _movies[id] = new MovieAggregate { TvdbId = id, Name = name, RuntimeMinutes = runtime, Genres = genres };

    private void WatchEpisode(int seriesId, int episodeId, DateTime when, WatchSource source = WatchSource.Single) =>
        _episodeWatches.Add(new EpisodeWatchRecord(seriesId, episodeId, when, source));

    private void WatchMovie(int movieId, DateTime when, WatchSource source = WatchSource.Single) =>
        _movieWatches.Add(new MovieWatch(movieId, when, source));

    // ---- nothing ----

    [Test]
    public void NothingWatchedOrRated_Should_BeEmpty_WithTwelveEmptyMonths()
    {
        var stats = Build();

        stats.IsEmpty.Should().BeTrue();
        stats.HasChart.Should().BeFalse();
        stats.Totals.Should().Be(new StatsTotals(0, 0, 0, 0));
        stats.Months.Should().HaveCount(12);
        stats.Months[0].Month.Should().Be(new DateOnly(2025, 11, 1));
        stats.Months[^1].Month.Should().Be(new DateOnly(2026, 10, 1));
        stats.TopSeries.Should().BeEmpty();
        stats.TopGenres.Should().BeEmpty();
        stats.Ratings.Count.Should().Be(0);
        stats.Gaps.Any.Should().BeFalse();
    }

    // ---- totals and runtime ----

    [Test]
    public void Totals_Should_SumEpisodeAndMovieRuntimes_AndCountEveryRow()
    {
        Series(1, "Show", averageRuntime: 45, episodes: [Ep(11, 1, 1, runtime: 50), Ep(12, 1, 2, runtime: 55)]);
        Movie(900, "Film", runtime: 120);
        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(1, 12, Utc(2026, 9, 2));
        WatchMovie(900, Utc(2026, 9, 3));

        var stats = Build();

        stats.Totals.Minutes.Should().Be(50 + 55 + 120);
        stats.Totals.Episodes.Should().Be(2);
        stats.Totals.Movies.Should().Be(1);
        stats.Totals.WatchTime.Across.Should().Be("2 episodes and 1 movie");
        stats.IsEmpty.Should().BeFalse();
    }

    [Test]
    public void AnEpisodeWithoutARuntime_Should_UseTheSeriesAverage()
    {
        Series(1, "Show", averageRuntime: 45, episodes: [Ep(11, 1, 1, runtime: null), Ep(12, 1, 2, runtime: 0)]);
        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(1, 12, Utc(2026, 9, 2));

        var stats = Build();

        stats.Totals.Minutes.Should().Be(90);
        stats.Gaps.EpisodesWithoutRuntime.Should().Be(0);
    }

    [Test]
    public void AnEpisodeTheAggregateDoesNotList_Should_StillCount_WithTheSeriesAverage()
    {
        // Watched, then dropped from TheTVDB's list (renumbered, merged).
        Series(1, "Show", averageRuntime: 45, episodes: [Ep(11, 1, 1, runtime: 50)]);
        WatchEpisode(1, 999, Utc(2026, 9, 1));

        var stats = Build();

        stats.Totals.Should().Be(new StatsTotals(45, 1, 0, 0));
    }

    [Test]
    public void AnEpisodeWithNoRuntimeAnywhere_Should_CountAsAnEpisode_AddNoTime_AndBeReported()
    {
        Series(1, "Show", averageRuntime: null, episodes: [Ep(11, 1, 1, runtime: null)]);
        WatchEpisode(1, 11, Utc(2026, 9, 1));

        var stats = Build();

        stats.Totals.Should().Be(new StatsTotals(0, 1, 0, 0));
        stats.Gaps.Should().Be(new StatsGaps(TitlesNotCached: 0, EpisodesWithoutRuntime: 1, MoviesWithoutRuntime: 0));
    }

    [Test]
    public void TitlesThatAreNotCached_Should_CountInTheTotals_AddNoTime_AndBeReportedOncePerTitle()
    {
        WatchEpisode(7, 71, Utc(2026, 9, 1));
        WatchEpisode(7, 72, Utc(2026, 9, 2));
        WatchMovie(900, Utc(2026, 9, 3));

        var stats = Build();

        stats.Totals.Should().Be(new StatsTotals(0, 2, 1, 0));
        stats.Gaps.TitlesNotCached.Should().Be(2, "one series and one movie");
        stats.TopSeries.Should().BeEmpty("a series without a cached record has no name to list");
        stats.Months.Single(m => m.Month == new DateOnly(2026, 9, 1)).Should().Be(new StatsMonth(new DateOnly(2026, 9, 1), 0, 2, 1));
    }

    [Test]
    public void AMovieWithoutARuntime_Should_CountAndBeReported()
    {
        Movie(900, "Film", runtime: null, "Drama");
        WatchMovie(900, Utc(2026, 9, 3));

        var stats = Build();

        stats.Totals.Should().Be(new StatsTotals(0, 0, 1, 0));
        stats.Gaps.MoviesWithoutRuntime.Should().Be(1);
    }

    [Test]
    public void Specials_AndMovieFlaggedEntries_Should_CountTowardTimeAndEpisodes()
    {
        // Time is time: a special never counts toward progress, but it was watched.
        Series(1, "Show", averageRuntime: 45, episodes:
        [
            Ep(10, 0, 1, runtime: 30),
            Ep(11, 1, 1, runtime: 50),
            Ep(19, 1, 9, runtime: 95, isMovie: true)
        ]);
        WatchEpisode(1, 10, Utc(2026, 9, 1));
        WatchEpisode(1, 11, Utc(2026, 9, 2));
        WatchEpisode(1, 19, Utc(2026, 9, 3));

        Build().Totals.Should().Be(new StatsTotals(175, 3, 0, 0));
    }

    // ---- the monthly chart and the source rule ----

    [Test]
    public void MonthlyChart_Should_CountSingleAndUnknownWatches_InTheirUtcMonth()
    {
        Series(1, "Show", averageRuntime: 60, episodes: [Ep(11, 1, 1), Ep(12, 1, 2), Ep(13, 1, 3)]);
        Movie(900, "Film", runtime: 100);
        WatchEpisode(1, 11, Utc(2026, 8, 30), WatchSource.Single);
        WatchEpisode(1, 12, Utc(2026, 9, 1), WatchSource.Unknown);
        WatchEpisode(1, 13, Utc(2026, 9, 28), WatchSource.Single);
        WatchMovie(900, Utc(2026, 9, 10), WatchSource.Single);

        var stats = Build();

        stats.Months.Single(m => m.Month == new DateOnly(2026, 8, 1)).Should().Be(new StatsMonth(new DateOnly(2026, 8, 1), 60, 1, 0));
        stats.Months.Single(m => m.Month == new DateOnly(2026, 9, 1)).Should().Be(new StatsMonth(new DateOnly(2026, 9, 1), 220, 2, 1));
        stats.Months.Where(m => m.Month != new DateOnly(2026, 8, 1) && m.Month != new DateOnly(2026, 9, 1))
            .Should().OnlyContain(m => m.Minutes == 0 && m.Episodes == 0 && m.Movies == 0);
        stats.HasChart.Should().BeTrue();
        stats.LargestMonthMinutes.Should().Be(220);
        stats.Undated.Any.Should().BeFalse();
    }

    [Test]
    public void BulkAndImportedWatches_Should_BeInTheTotalsAndTopLists_ButNotOnTheChart()
    {
        // A season marked in one go, and an IMDb import: forty hours "on one day".
        Series(1, "Show", averageRuntime: 60, genres: ["Drama"], episodes: [Ep(11, 1, 1), Ep(12, 1, 2), Ep(13, 1, 3)]);
        Movie(900, "Film", runtime: 100, "Drama");
        WatchEpisode(1, 11, Utc(2026, 9, 4), WatchSource.Bulk);
        WatchEpisode(1, 12, Utc(2026, 9, 4), WatchSource.Bulk);
        WatchEpisode(1, 13, Utc(2026, 9, 4), WatchSource.Bulk);
        WatchMovie(900, Utc(2026, 9, 12), WatchSource.Import);

        var stats = Build();

        stats.Totals.Should().Be(new StatsTotals(280, 3, 1, 0));
        stats.TopSeries.Should().ContainSingle().Which.Should().Be(new StatsSeriesRow(1, "Show", 180, 3));
        stats.TopGenres.Should().ContainSingle().Which.Should().Be(new StatsGenreRow("Drama", 280, 2));

        stats.HasChart.Should().BeFalse("none of these has a date that says when it was watched");
        stats.Months.Should().OnlyContain(m => m.Minutes == 0 && m.Episodes == 0 && m.Movies == 0);
        stats.Undated.Should().Be(new StatsUndated(Episodes: 3, Movies: 1));
    }

    [Test]
    public void MarkThisAndEarlier_Should_ChartTheClickedEpisodeOnly()
    {
        // What the write path records: the clicked episode Single, the earlier ones Bulk.
        Series(1, "Show", averageRuntime: 60, episodes: [Ep(11, 1, 1), Ep(12, 1, 2), Ep(13, 1, 3)]);
        var when = Utc(2026, 9, 4);
        WatchEpisode(1, 11, when, WatchSource.Bulk);
        WatchEpisode(1, 12, when, WatchSource.Bulk);
        WatchEpisode(1, 13, when, WatchSource.Single);

        var stats = Build();

        stats.Months.Single(m => m.Month == new DateOnly(2026, 9, 1)).Should().Be(new StatsMonth(new DateOnly(2026, 9, 1), 60, 1, 0));
        stats.Undated.Should().Be(new StatsUndated(2, 0));
        stats.Totals.Episodes.Should().Be(3);
    }

    [Test]
    public void DatedWatches_OutsideTheWindow_Should_BeInTheTotals_AndNotCountAsLeftOffTheChart()
    {
        Series(1, "Show", averageRuntime: 60, episodes: [Ep(11, 1, 1), Ep(12, 1, 2)]);
        WatchEpisode(1, 11, Utc(2025, 10, 31));   // the month before the window opens
        WatchEpisode(1, 12, Utc(2025, 11, 1));    // its first day

        var stats = Build();

        stats.Totals.Episodes.Should().Be(2);
        stats.Months.Sum(m => m.Episodes).Should().Be(1);
        stats.Months[0].Episodes.Should().Be(1);
        stats.Undated.Any.Should().BeFalse("the older watch has a real date; it is only older than the chart");
    }

    [Test]
    public void Months_Should_BeJudgedInUtc()
    {
        Series(1, "Show", averageRuntime: 60, episodes: [Ep(11, 1, 1), Ep(12, 1, 2)]);
        WatchEpisode(1, 11, new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc));
        WatchEpisode(1, 12, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = Build();

        stats.Months.Single(m => m.Month == new DateOnly(2026, 9, 1)).Episodes.Should().Be(1);
        stats.Months.Single(m => m.Month == new DateOnly(2026, 10, 1)).Episodes.Should().Be(1);
    }

    [Test]
    public void ACalendarYearWindow_Should_CoverJanuaryToDecember()
    {
        Series(1, "Show", averageRuntime: 60, episodes: [Ep(11, 1, 1), Ep(12, 1, 2)]);
        WatchEpisode(1, 11, Utc(2025, 12, 31));
        WatchEpisode(1, 12, Utc(2026, 1, 1));

        var stats = StatsBuilder.Build(
            new StatsInput(_episodeWatches, _movieWatches, _series, _movies, _tracked, _ratings),
            Now,
            StatsWindow.CalendarYear(2026));

        stats.Months.Select(m => m.Month.Month).Should().Equal(Enumerable.Range(1, 12));
        stats.Months.Should().OnlyContain(m => m.Month.Year == 2026);
        stats.Months.Sum(m => m.Episodes).Should().Be(1);
    }

    // ---- top series ----

    [Test]
    public void TopSeries_Should_BeTheFiveWithTheMostWatchTime_ThenEpisodes_ThenName()
    {
        for (var i = 1; i <= 7; i++)
        {
            // Series i: i episodes of 30 minutes, except series 7, which is all 5-minute shorts.
            var runtime = i == 7 ? 5 : 30;
            Series(i, $"Show {i}", averageRuntime: runtime,
                episodes: Enumerable.Range(1, i).Select(n => Ep(i * 100 + n, 1, n)).ToArray());
            for (var n = 1; n <= i; n++)
                WatchEpisode(i, i * 100 + n, Utc(2026, 9, n), WatchSource.Bulk);
        }

        var top = Build().TopSeries;

        top.Select(r => r.Name).Should().Equal("Show 6", "Show 5", "Show 4", "Show 3", "Show 2");
        top[0].Should().Be(new StatsSeriesRow(6, "Show 6", 180, 6));
    }

    [Test]
    public void TopSeries_Should_BreakATieOnTime_ByEpisodes_ThenByName()
    {
        Series(1, "Beta", averageRuntime: 60, episodes: [Ep(11, 1, 1)]);
        Series(2, "Alpha", averageRuntime: 60, episodes: [Ep(21, 1, 1)]);
        Series(3, "Gamma", averageRuntime: 30, episodes: [Ep(31, 1, 1), Ep(32, 1, 2)]);
        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(2, 21, Utc(2026, 9, 1));
        WatchEpisode(3, 31, Utc(2026, 9, 1));
        WatchEpisode(3, 32, Utc(2026, 9, 1));

        Build().TopSeries.Select(r => r.Name).Should().Equal("Gamma", "Alpha", "Beta");
    }

    // ---- genres ----

    [Test]
    public void Genres_Should_CountATitlesTimeInFull_UnderEachOfItsGenres_AcrossSeriesAndMovies()
    {
        Series(1, "Show", averageRuntime: 60, genres: ["Drama", "Crime"], episodes: [Ep(11, 1, 1), Ep(12, 1, 2)]);
        Movie(900, "Film", runtime: 100, "Drama", "Thriller");
        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(1, 12, Utc(2026, 9, 2));
        WatchMovie(900, Utc(2026, 9, 3));

        var stats = Build();

        stats.TopGenres.Should().Equal(
            new StatsGenreRow("Drama", 220, 2),
            new StatsGenreRow("Crime", 120, 1),
            new StatsGenreRow("Thriller", 100, 1));
        stats.GenreCoverage.Should().Be(new StatsGenreCoverage(TitlesWithGenres: 2, Titles: 2));
        stats.GenreCoverage.IsComplete.Should().BeTrue();
    }

    [Test]
    public void Genres_Should_ReportCoverage_WhenSomeTitlesHaveNone()
    {
        // A series cached before series carried genres, and a movie that is not cached at all.
        Series(1, "Old Row", averageRuntime: 60, genres: [], episodes: [Ep(11, 1, 1)]);
        Series(2, "New Row", averageRuntime: 60, genres: ["Comedy"], episodes: [Ep(21, 1, 1)]);
        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(2, 21, Utc(2026, 9, 1));
        WatchMovie(900, Utc(2026, 9, 3));

        var stats = Build();

        stats.TopGenres.Should().Equal(new StatsGenreRow("Comedy", 60, 1));
        stats.GenreCoverage.Should().Be(new StatsGenreCoverage(TitlesWithGenres: 1, Titles: 3));
        stats.GenreCoverage.IsComplete.Should().BeFalse();
    }

    [Test]
    public void Genres_Should_KeepTheSixLargest_MergeCase_AndDropThoseWithNoTime()
    {
        string[] names = ["A", "B", "C", "D", "E", "F", "G"];
        for (var i = 0; i < names.Length; i++)
        {
            Movie(900 + i, $"Film {i}", runtime: 100 + i, names[i]);
            WatchMovie(900 + i, Utc(2026, 9, 1));
        }

        Movie(950, "Lowercase", runtime: 10, "a");
        WatchMovie(950, Utc(2026, 9, 1));
        Movie(951, "No Length", runtime: null, "Western");
        WatchMovie(951, Utc(2026, 9, 1));

        var genres = Build().TopGenres;

        genres.Select(g => g.Name).Should().Equal("A", "G", "F", "E", "D", "C");
        genres[0].Should().Be(new StatsGenreRow("A", 110, 2), "'a' and 'A' are one genre");
    }

    [Test]
    public void Genres_Should_LeaveOutFormatLabels_SuchAsMiniSeries()
    {
        // TheTVDB files "Mini-Series" under genres; it says what a title is, not what it is about.
        Series(1, "Chernobyl", averageRuntime: 60, genres: ["Mini-Series", "Drama", "History"], episodes: [Ep(11, 1, 1)]);
        Series(2, "Only A Format", averageRuntime: 60, genres: ["mini-series"], episodes: [Ep(21, 1, 1)]);
        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(2, 21, Utc(2026, 9, 1));

        var stats = Build();

        stats.TopGenres.Select(g => g.Name).Should().Equal("Drama", "History");
        stats.GenreCoverage.Should().Be(
            new StatsGenreCoverage(TitlesWithGenres: 2, Titles: 2),
            "both titles have their genres loaded; the note is about titles that do not");
        StatsBuilder.FormatLabels.Should().Contain("Mini-Series");
    }

    // ---- series finished ----

    [Test]
    public void SeriesFinished_Should_CountTrackedEndedSeries_WithNoAiredRegularEpisodeLeft()
    {
        Series(1, "Finished", 60, status: "Ended", episodes: [Ep(11, 1, 1), Ep(12, 1, 2), Ep(10, 0, 1)]);
        Series(2, "Still Watching", 60, status: "Ended", episodes: [Ep(21, 1, 1), Ep(22, 1, 2)]);
        Series(3, "Up To Date", 60, status: "Continuing", episodes: [Ep(31, 1, 1)]);
        Series(4, "Finished But Not In The Library", 60, status: "Ended", episodes: [Ep(41, 1, 1)]);
        _tracked.UnionWith([1, 2, 3]);

        WatchEpisode(1, 11, Utc(2026, 9, 1));
        WatchEpisode(1, 12, Utc(2026, 9, 2));      // the special (10) is unwatched and does not matter
        WatchEpisode(2, 21, Utc(2026, 9, 1));
        WatchEpisode(3, 31, Utc(2026, 9, 1));
        WatchEpisode(4, 41, Utc(2026, 9, 1));

        Build().Totals.SeriesFinished.Should().Be(1);
    }

    [Test]
    public void SeriesFinished_Should_IgnoreEpisodesThatHaveNotAired_AndSeriesNeverStarted()
    {
        Series(1, "Ended With A Late Special Episode", 60, status: "Ended",
            episodes: [Ep(11, 1, 1), Ep(12, 1, 2, aired: "2026-12-24")]);
        Series(2, "Ended, Never Started", 60, status: "Ended", episodes: [Ep(21, 1, 1, aired: "2027-01-01")]);
        _tracked.UnionWith([1, 2, 3]);       // 3 is tracked but not cached
        WatchEpisode(1, 11, Utc(2026, 9, 1));

        Build().Totals.SeriesFinished.Should().Be(1, "series 2 has nothing watched, so it was not finished by anyone");
    }

    [Test]
    public void SeriesFinished_Should_NotCountAnEndedSeries_OfWhichOnlySpecialsWereWatched_OrNothingAired()
    {
        // The Library lists none of these under Watched either (the same rule).
        Series(1, "Only The Special Watched", 60, status: "Ended", episodes: [Ep(10, 0, 1), Ep(11, 1, 1)]);
        Series(2, "Only A Special Exists", 60, status: "Ended", episodes: [Ep(20, 0, 1)]);
        Series(3, "Fully Watched", 60, status: "Ended", episodes: [Ep(30, 0, 1), Ep(31, 1, 1)]);
        _tracked.UnionWith([1, 2, 3]);
        WatchEpisode(1, 10, Utc(2026, 9, 1));
        WatchEpisode(2, 20, Utc(2026, 9, 1));
        WatchEpisode(3, 31, Utc(2026, 9, 1));

        Build().Totals.SeriesFinished.Should().Be(1);
    }

    // ---- ratings ----

    [Test]
    public void Ratings_Should_GiveTheDistribution_TheCount_AndTheAverage()
    {
        _ratings[10] = 2;
        _ratings[7] = 3;
        _ratings[1] = 1;

        var ratings = Build().Ratings;

        ratings.CountByValue.Should().Equal(1, 0, 0, 0, 0, 0, 3, 0, 0, 2);
        ratings.Count.Should().Be(6);
        ratings.Average.Should().BeApproximately((20 + 21 + 1) / 6.0, 1e-9);
        ratings.LargestCount.Should().Be(3);
    }

    [Test]
    public void Ratings_Should_IgnoreValuesOutsideOneToTen()
    {
        _ratings[0] = 4;
        _ratings[11] = 4;
        _ratings[5] = 1;

        var ratings = Build().Ratings;

        ratings.Count.Should().Be(1);
        ratings.Average.Should().Be(5);
    }

    [Test]
    public void OnlyRatings_Should_NotBeEmpty()
    {
        _ratings[8] = 1;

        var stats = Build();

        stats.IsEmpty.Should().BeFalse();
        stats.Totals.Should().Be(new StatsTotals(0, 0, 0, 0));
    }
}
