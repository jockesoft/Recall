using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Pages.Shared;

namespace Recall.Tests.Pages;

[TestFixture]
public sealed class TitleSummaryTests
{
    private static SeriesAggregate Series(DateOnly? first, DateOnly? last, string status, bool keepUpdated, params int?[] seasons) =>
        new()
        {
            FirstAired = first,
            LastAired = last,
            Status = new SeriesStatus { Name = status, KeepUpdated = keepUpdated },
            Seasons = seasons.Select((n, i) => new SeasonSummary { Id = i + 1, Number = n }).ToList()
        };

    [Test]
    public void ForSeries_Should_ShowTheYearRange_Status_AndSeasonCount_ForAnEndedSeries()
    {
        var series = Series(new DateOnly(2008, 1, 20), new DateOnly(2013, 9, 29), "Ended", keepUpdated: false, 0, 1, 2, 3, 4, 5);

        TitleSummary.ForSeries(series).Should().Be("2008–2013 · Ended · 5 seasons", "the specials are not a season");
    }

    [Test]
    public void ForSeries_Should_LeaveTheRangeOpen_WhileTheSeriesContinues()
    {
        var series = Series(new DateOnly(2022, 2, 18), new DateOnly(2025, 3, 21), "Continuing", keepUpdated: true, 1, 2);

        TitleSummary.ForSeries(series).Should().Be("2022– · Continuing · 2 seasons");
    }

    [Test]
    public void ForSeries_Should_ShowOneYear_AndSingularSeason_ForAMiniSeries()
    {
        var series = Series(new DateOnly(2019, 5, 6), new DateOnly(2019, 6, 3), "Ended", keepUpdated: false, 1);

        TitleSummary.ForSeries(series).Should().Be("2019 · Ended · 1 season");
    }

    [Test]
    public void ForSeries_Should_LeaveOutWhatIsUnknown()
    {
        TitleSummary.ForSeries(new SeriesAggregate()).Should().BeNull();
        TitleSummary.ForSeries(new SeriesAggregate { Status = new SeriesStatus { Name = "Upcoming" } }).Should().Be("Upcoming");
    }

    [Test]
    public void ForMovie_Should_ShowYearAndRuntime()
    {
        var movie = new MovieAggregate { ReleaseDate = new DateOnly(2023, 7, 19), RuntimeMinutes = 189 };

        TitleSummary.ForMovie(movie).Should().Be("2023 · 3h 9m");
    }

    [Test]
    public void ForMovie_Should_LeaveOutWhatIsUnknown()
    {
        TitleSummary.ForMovie(new MovieAggregate { RuntimeMinutes = 45 }).Should().Be("45m");
        TitleSummary.ForMovie(new MovieAggregate { Year = "1995" }).Should().Be("1995");
        TitleSummary.ForMovie(new MovieAggregate()).Should().BeNull();
    }
}
