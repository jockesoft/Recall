using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services.Discover;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Services.Discover;

/// <summary>
/// The small rules behind Discover's lists: which of a user's genres get a
/// row (the first two eligible of their Stats ranking), what a title needs to
/// be suggested, and which lists a date needs.
/// </summary>
[TestFixture]
public sealed class DiscoverRulesTests
{
    private static readonly DateOnly November = new(2026, 11, 25);

    // ---- the user's genres ----------------------------------------------------------

    [Test]
    public void TheGenreRows_Should_BeTheUsersTopTwo_ByWatchTime()
    {
        var genres = DiscoverGenres.ForUser(Stats(10, "Crime", "Drama", "Comedy"), minTitlesWithGenres: 5);

        genres.Should().Equal(new DiscoverGenre("Crime", 14), new DiscoverGenre("Drama", 12));
    }

    [Test]
    public void GenresThatNeverGetARow_Should_BeSkipped_ForTheNextOnes()
    {
        var genres = DiscoverGenres.ForUser(Stats(10, "News", "Talk Show", "Podcast", "Awards Show", "Mini-Series", "Reality", "Food"), 5);

        genres.Select(g => g.Name).Should().Equal(["Reality", "Food"], "Reality stays in; the others are not stories to discover");
    }

    [Test]
    public void AGenreTheVTDBDoesNotKnow_Should_BeSkipped()
    {
        DiscoverGenres.ForUser(Stats(10, "Sci-Fi", "Western"), 5).Select(g => g.Name).Should().Equal("Western");
    }

    [Test]
    public void OneEligibleGenre_Should_GiveOneRow()
    {
        DiscoverGenres.ForUser(Stats(10, "History"), 5).Should().ContainSingle().Which.Id.Should().Be(33);
    }

    [TestCase(0)]
    [TestCase(4)]
    public void TooFewTitlesWithGenres_Should_GiveNoGenreRows(int titlesWithGenres)
    {
        DiscoverGenres.ForUser(Stats(titlesWithGenres, "Crime", "Drama"), minTitlesWithGenres: 5).Should().BeEmpty();
    }

    [Test]
    public void EveryGenreName_Should_HaveADistinctTheTvdbId()
    {
        DiscoverGenres.Ids.Should().HaveCount(35);
        DiscoverGenres.Ids.Values.Should().OnlyHaveUniqueItems().And.NotContain(20, "TheTVDB has no genre 20");
    }

    // ---- quality --------------------------------------------------------------------

    [Test]
    public void ATitleWithAPosterAScoreAndASaneYear_Should_Pass()
    {
        DiscoverQuality.Passes(Title(), November, minScore: 100).Should().BeTrue();
        DiscoverQuality.Passes(Title() with { Year = 2027, FirstAired = new DateOnly(2027, 2, 1) }, November, 100)
            .Should().BeTrue("next year's titles are real");
    }

    [Test]
    public void NoPoster_Should_Fail() =>
        DiscoverQuality.Passes(Title() with { ImageUrl = null }, November, 100).Should().BeFalse();

    [Test]
    public void AScoreBelowTheMinimum_Should_Fail()
    {
        DiscoverQuality.Passes(Title() with { Score = 99 }, November, 100).Should().BeFalse();
        DiscoverQuality.Passes(Title() with { Score = 100 }, November, 100).Should().BeTrue("the minimum itself is enough");
    }

    [TestCase(9696)]
    [TestCase(2200)]
    [TestCase(2028)]
    [TestCase(1899)]
    public void AnInsaneYear_Should_Fail(int year) =>
        DiscoverQuality.Passes(Title() with { Year = year, FirstAired = null }, November, 100).Should().BeFalse();

    [Test]
    public void NoYear_Should_Fail() =>
        DiscoverQuality.Passes(Title() with { Year = null }, November, 100).Should().BeFalse();

    [Test]
    public void AFirstAirDateFarInTheFuture_Should_Fail() =>
        DiscoverQuality.Passes(Title() with { Year = 2026, FirstAired = new DateOnly(3013, 12, 16) }, November, 100).Should().BeFalse();

    // ---- which lists a date needs ---------------------------------------------------

    [Test]
    public void InNovember_Should_NeedThisYearsListsAndAllTime()
    {
        DiscoverListKey.Fixed(November, new DiscoverOptions()).Select(k => k.Key).Should().BeEquivalentTo(
            "series-new-2026", "series-popular-2026", "movie-year-2026", "series-alltime", "movie-alltime");
    }

    [Test]
    public void InFebruary_Should_AlsoNeedLastYearsLists()
    {
        DiscoverListKey.Fixed(new DateOnly(2027, 2, 10), new DiscoverOptions()).Select(k => k.Key).Should().BeEquivalentTo(
            "series-new-2027", "series-popular-2027", "movie-year-2027", "series-alltime", "movie-alltime",
            "series-new-2026", "series-popular-2026", "movie-year-2026");
    }

    [Test]
    public void InMay_Should_NeedLastYearsPopularLists_ButNotItsNewSeries()
    {
        var keys = DiscoverListKey.Fixed(new DateOnly(2027, 5, 10), new DiscoverOptions()).Select(k => k.Key).ToList();

        keys.Should().Contain(["series-popular-2026", "movie-year-2026"]);
        keys.Should().NotContain("series-new-2026", "ninety days back from May 10 is still this year");
    }

    [Test]
    public void TheLists_Should_AskTheRightQuestions()
    {
        DiscoverListKey.SeriesNew(2026).Filter.SeriesPath().Should().Be("series/filter?lang=eng&sort=firstAired&sortType=desc&year=2026");
        DiscoverListKey.SeriesPopular(2026).Filter.SeriesPath().Should().Be("series/filter?lang=eng&sort=score&sortType=desc&year=2026");
        DiscoverListKey.MoviesOfYear(2026).Filter.MoviesPath().Should().Be("movies/filter?lang=eng&sort=score&year=2026");
        DiscoverListKey.SeriesGenre(14).Filter.SeriesPath().Should().Be("series/filter?lang=eng&sort=score&sortType=desc&genre=14");
        DiscoverListKey.MoviesGenre(14).Filter.MoviesPath().Should().Be("movies/filter?lang=eng&sort=score&genre=14");
        DiscoverListKey.MoviesAllTime.Filter.MoviesPath().Should().Be("movies/filter?lang=eng&sort=score");
    }

    [Test]
    public void GenreLists_Should_KeepAWholePage_AndOrderedListsLess()
    {
        DiscoverListKey.SeriesGenre(14).Keep.Should().Be(500, "a genre row puts recent titles first from the whole page");
        DiscoverListKey.MoviesGenre(14).Keep.Should().Be(500);
        DiscoverListKey.SeriesNew(2026).Keep.Should().Be(500, "the 90 days are picked from the page when the page is read");
        DiscoverListKey.SeriesPopular(2026).Keep.Should().Be(150);
    }

    private static DiscoverTitle Title() => new()
    {
        Kind = DiscoverKind.Series, TvdbId = 1, Name = "Severance", Year = 2026, FirstAired = new DateOnly(2026, 3, 1),
        ImageUrl = "https://artworks.thetvdb.com/a.jpg", Status = "Continuing", Score = 5000
    };

    private static UserStats Stats(int titlesWithGenres, params string[] genres) =>
        UserStats.Empty with
        {
            TopGenres = genres.Select((name, i) => new StatsGenreRow(name, 1000 - i, 3)).ToList(),
            GenreCoverage = new StatsGenreCoverage(titlesWithGenres, titlesWithGenres)
        };
}
