using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Discover;

namespace Recall.Tests.Services.Discover;

/// <summary>
/// The Discover rows (<see cref="DiscoverBuilder"/>): their order and wording,
/// what each row takes from the stored lists, the year a "… from" row is
/// about, the genre rows' year bands, and the rules every row shares: nothing
/// the user knows or hid, no title twice, a thin row hidden.
/// </summary>
[TestFixture]
public sealed class DiscoverBuilderTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);
    private static readonly DiscoverGenre Crime = new("Crime", 14);
    private static readonly DiscoverGenre Drama = new("Drama", 12);

    private static readonly DiscoverOptions Options = new() { MaxPerRow = 20, MinPerRow = 6, NewSeriesDays = 90, RecentYears = 5 };

    private Dictionary<string, DiscoverList> _lists = null!;
    private int _nextId;

    [SetUp]
    public void SetUp()
    {
        _lists = new Dictionary<string, DiscoverList>();
        _nextId = 1000;
    }

    [Test]
    public void NoStoredList_Should_SaySuggestionsAreOnTheirWay()
    {
        var page = DiscoverBuilder.Build(new DiscoverInput(_lists, KnownTitles.None, [Crime]), Today, Options);

        page.HasLists.Should().BeFalse();
        page.Rows.Should().BeEmpty();
    }

    [Test]
    public void AFullPage_Should_HaveItsRowsInOrder_WithTheirWords()
    {
        FillEveryList(genres: [Crime, Drama]);

        var page = Build(Crime, Drama);

        page.HasLists.Should().BeTrue();
        page.Rows.Select(r => r.Heading).Should().Equal(
            "New series", "Popular from 2026", "Popular in Crime", "Popular in Drama", "Movies from 2026");
        page.Rows.Select(r => r.Slug).Should().Equal("new", "popular", "genre-crime", "genre-drama", "movies");
        page.Rows.Select(r => r.Description).Should().Equal(
            "Premiered in the last 90 days, the most popular first.",
            "Series that premiered in 2026, the most popular first.",
            "Crime series and movies, the last 5 years first.",
            "Drama series and movies, the last 5 years first.",
            "Released in 2026, the most popular first.");
        page.Rows.Should().OnlyContain(r => r.Titles.Count == 20, "every list has more than a row's worth");
    }

    // ---- New series -----------------------------------------------------------------

    [Test]
    public void NewSeries_Should_BeThoseThatPremieredInTheLast90Days_MostPopularFirst()
    {
        Store("series-new-2026",
            Series("Ninety days ago", aired: Today.AddDays(-90), score: 100),
            Series("Ninety-one days ago", aired: Today.AddDays(-91), score: 9000),
            Series("Tomorrow", aired: Today.AddDays(1), score: 9000),
            Series("Today", aired: Today, score: 500),
            Series("Last week", aired: Today.AddDays(-7), score: 2000),
            Series("Last month", aired: Today.AddDays(-30), score: 1000),
            Series("Two months ago", aired: Today.AddDays(-60), score: 300),
            Series("Yesterday", aired: Today.AddDays(-1), score: 200));

        Row(Build(), "new").Titles.Select(t => t.Name).Should().Equal(
            "Last week", "Last month", "Today", "Two months ago", "Yesterday", "Ninety days ago");
    }

    [Test]
    public void NewSeries_InJanuary_Should_ReachBackIntoLastYear()
    {
        var january = new DateOnly(2027, 1, 20);
        Store("series-new-2027", Enumerable.Range(1, 3).Select(i => Series($"This year {i}", aired: january.AddDays(-i))).ToArray());
        Store("series-new-2026", Enumerable.Range(1, 5).Select(i => Series($"Last December {i}", aired: new DateOnly(2026, 12, 20 - i))).ToArray());

        var row = Row(DiscoverBuilder.Build(new DiscoverInput(_lists, KnownTitles.None, []), january, Options), "new");

        row.Titles.Should().HaveCount(8);
    }

    // ---- Popular from {year} --------------------------------------------------------

    [Test]
    public void PopularFrom_Should_KeepTheListsOrder_AndLeaveOutWhatHasNotPremieredYet()
    {
        var titles = Enumerable.Range(1, 8).Select(i => Series($"Popular {i}", aired: new DateOnly(2026, 3, i), score: 1000 - i)).ToList();
        titles.Insert(2, Series("Harry Potter", aired: new DateOnly(2026, 12, 25), score: 37791));
        Store("series-popular-2026", titles.ToArray());

        Row(Build(), "popular").Titles.Select(t => t.Name).Should().Equal(Enumerable.Range(1, 8).Select(i => $"Popular {i}"));
    }

    [Test]
    public void PopularFrom_EarlyInTheYear_Should_FallBackToLastYear_WhileThisYearCannotFillARow()
    {
        var february = new DateOnly(2027, 2, 10);
        Store("series-popular-2027", Enumerable.Range(1, 8).Select(i => Series($"New {i}", aired: new DateOnly(2027, 1, i))).ToArray());
        Store("series-popular-2026", Enumerable.Range(1, 30).Select(i => Series($"Last year {i}", aired: new DateOnly(2026, 3, 1))).ToArray());

        var row = Row(DiscoverBuilder.Build(new DiscoverInput(_lists, KnownTitles.None, []), february, Options), "popular");

        row.Heading.Should().Be("Popular from 2026");
        row.Description.Should().Be("Series that premiered in 2026, the most popular first.");
        row.Titles.Should().OnlyContain(t => t.Name.StartsWith("Last year"), "a row is about one year, never both");
    }

    [Test]
    public void PopularFrom_Should_BeThisYear_OnceItFillsARow()
    {
        var march = new DateOnly(2027, 3, 10);
        Store("series-popular-2027", Enumerable.Range(1, 20).Select(i => Series($"New {i}", aired: new DateOnly(2027, 1, i))).ToArray());
        Store("series-popular-2026", Enumerable.Range(1, 30).Select(i => Series($"Last year {i}", aired: new DateOnly(2026, 3, 1))).ToArray());

        Row(DiscoverBuilder.Build(new DiscoverInput(_lists, KnownTitles.None, []), march, Options), "popular")
            .Heading.Should().Be("Popular from 2027");
    }

    [TestCase(25, 0, 2026)]
    [TestCase(20, 50, 2026)]
    [TestCase(10, 50, 2025)]
    [TestCase(10, 5, 2026)]
    [TestCase(0, 0, 2026)]
    public void ChooseYear_Should_PreferThisYear_UnlessItIsThinAndLastYearHasMore(int thisYear, int lastYear, int expected)
    {
        DiscoverBuilder.ChooseYear(2026, y => y == 2026 ? thisYear : lastYear, Options).Should().Be(expected);
    }

    // ---- Popular in {genre} ---------------------------------------------------------

    [Test]
    public void AGenreRow_Should_MixSeriesAndMovies_TheLastFiveYearsFirst_ByScoreWithinEach()
    {
        Store("series-genre-14",
            Series("Breaking Bad", year: 2008, score: 3_776_757),
            Series("The Pitt", year: 2025, score: 425_583),
            Series("Mare of Easttown", year: 2021, score: 600_000),   // 2026 − 5: older
            Series("Slow Horses", year: 2022, score: 300_000),        // the first of the five years
            Series("The Sopranos", year: 1999, score: 2_000_000));
        Store("movie-genre-14",
            Movie("The Godfather", year: 1972, score: 3_000_000),
            Movie("Weapons", year: 2026, score: 500_000),
            Movie("Heat", year: 1995, score: 900_000));

        var page = DiscoverBuilder.Build(new DiscoverInput(_lists, KnownTitles.None, [Crime]), Today, new DiscoverOptions { MinPerRow = 1, RecentYears = 5 });

        Row(page, "genre-crime").Titles.Select(t => t.Name).Should().Equal(
            "Weapons", "The Pitt", "Slow Horses",
            "Breaking Bad", "The Godfather", "The Sopranos", "Heat", "Mare of Easttown");
    }

    [Test]
    public void TheUsersTopTwoGenres_Should_EachGetARow_InTheirOrder()
    {
        FillEveryList(genres: [Crime, Drama]);

        var page = Build(Drama, Crime);

        page.Rows.Select(r => r.Heading).Where(h => h.StartsWith("Popular in")).Should().Equal("Popular in Drama", "Popular in Crime");
    }

    [Test]
    public void AGenreNotFetchedYet_Should_HaveNoRow_AndTheOtherStays()
    {
        FillEveryList(genres: [Crime]);

        Build(Crime, Drama).Rows.Select(r => r.Slug).Should().Equal("new", "popular", "genre-crime", "movies");
    }

    [Test]
    public void NoGenres_Should_GiveAllTimeFavourites_InTheirPlace()
    {
        FillEveryList(genres: []);

        var page = Build();

        page.Rows.Select(r => r.Slug).Should().Equal("new", "popular", "favourites", "movies");
        var favourites = Row(page, "favourites");
        favourites.Heading.Should().Be("All-time favourites");
        favourites.Description.Should().Be("The most popular series and movies on TheTVDB.");
        favourites.Titles.Select(t => t.Kind).Distinct().Should().BeEquivalentTo([DiscoverKind.Series, DiscoverKind.Movie]);
        favourites.Titles.Select(t => t.Score).Should().BeInDescendingOrder();
    }

    [Test]
    public void GenresWhoseListsAreNotFetchedYet_Should_GiveAllTimeFavourites()
    {
        FillEveryList(genres: []);

        Build(Crime).Rows.Select(r => r.Slug).Should().Contain("favourites").And.NotContain("genre-crime");
    }

    [Test]
    public void GenreRowsTooThinToShow_Should_GiveAllTimeFavourites()
    {
        FillEveryList(genres: []);
        Store("series-genre-14", Series("Only one", year: 2024));

        Build(Crime).Rows.Select(r => r.Slug).Should().Contain("favourites");
    }

    // ---- Movies from {year} ---------------------------------------------------------

    [Test]
    public void MoviesFrom_Should_BeReleasedMoviesOfThatYear()
    {
        Store("movie-year-2026",
            Movie("Avengers: Doomsday", year: 2026, status: "Filming / Post-Production", score: 69_813),
            Movie("Zootopia 2", year: 2025, score: 14_749),
            Movie("Moana", year: 2026), Movie("Toy Story 5", year: 2026), Movie("Scream 7", year: 2026),
            Movie("Supergirl", year: 2026), Movie("Project Hail Mary", year: 2026), Movie("The Odyssey", year: 2026));

        Row(Build(), "movies").Titles.Select(t => t.Name).Should().Equal(
            "Moana", "Toy Story 5", "Scream 7", "Supergirl", "Project Hail Mary", "The Odyssey");
    }

    // ---- what every row shares ------------------------------------------------------

    [Test]
    public void ATitleTheUserKnowsOrHid_Should_BeInNoRow()
    {
        FillEveryList(genres: [Crime]);
        var everywhereSeries = Series("Severance", aired: Today.AddDays(-10), year: 2026, score: 9_000_000);
        var everywhereMovie = Movie("Dune: Part Two", year: 2026, score: 9_000_000);
        foreach (var key in new[] { "series-new-2026", "series-popular-2026", "series-genre-14", "series-alltime" })
            Prepend(key, everywhereSeries);
        foreach (var key in new[] { "movie-genre-14", "movie-year-2026", "movie-alltime" })
            Prepend(key, everywhereMovie);

        var known = new KnownTitles(new HashSet<int> { everywhereSeries.TvdbId }, new HashSet<int> { everywhereMovie.TvdbId });
        var page = DiscoverBuilder.Build(new DiscoverInput(_lists, known, [Crime]), Today, Options);

        page.Rows.SelectMany(r => r.Titles).Should().NotContain(t => t.TvdbId == everywhereSeries.TvdbId || t.TvdbId == everywhereMovie.TvdbId);
        page.Rows.Should().HaveCount(4, "the rows are filled from the rest");
    }

    [Test]
    public void ATitleInSeveralLists_Should_BeShownOnce_InTheFirstRow()
    {
        FillEveryList(genres: [Crime]);
        var both = Series("Severance", aired: Today.AddDays(-10), year: 2026, score: 9_000_000);
        Prepend("series-new-2026", both);
        Prepend("series-popular-2026", both);
        Prepend("series-genre-14", both);

        var page = Build(Crime);

        page.Rows.SelectMany(r => r.Titles).Count(t => t.TvdbId == both.TvdbId).Should().Be(1);
        Row(page, "new").Titles[0].TvdbId.Should().Be(both.TvdbId);
        page.Rows.SelectMany(r => r.Titles).Select(t => (t.Kind, t.TvdbId)).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void ATitleBeyondARowsCap_Should_StillBeFreeForALaterRow()
    {
        var titles = Enumerable.Range(1, 21).Select(i => Series($"New {i}", aired: Today.AddDays(-i), year: 2026, score: 10_000 - i)).ToArray();
        Store("series-new-2026", titles);
        Store("series-popular-2026", [titles[20], .. Enumerable.Range(1, 6).Select(i => Series($"Other {i}", aired: new DateOnly(2026, 2, i)))]);

        var page = Build();

        Row(page, "new").Titles.Should().HaveCount(20);
        Row(page, "popular").Titles[0].Name.Should().Be("New 21", "only the titles a row shows are taken");
    }

    [Test]
    public void ARowWithFewerThanSixTitles_Should_BeHidden()
    {
        Store("series-new-2026", Enumerable.Range(1, 5).Select(i => Series($"New {i}", aired: Today.AddDays(-i))).ToArray());
        Store("series-popular-2026", Enumerable.Range(1, 6).Select(i => Series($"Popular {i}", aired: new DateOnly(2026, 2, i))).ToArray());

        Build().Rows.Select(r => r.Slug).Should().Equal("popular");
    }

    [Test]
    public void ListsWhereTheUserKnowsEverything_Should_LeaveNoRows_ButStillCountAsFetched()
    {
        var title = Series("Known", aired: Today.AddDays(-3));
        Store("series-new-2026", title);

        var page = DiscoverBuilder.Build(
            new DiscoverInput(_lists, new KnownTitles(new HashSet<int> { title.TvdbId }, new HashSet<int>()), []), Today, Options);

        page.HasLists.Should().BeTrue();
        page.Rows.Should().BeEmpty();
    }

    [TestCase("Crime", "genre-crime")]
    [TestCase("Science Fiction", "genre-science-fiction")]
    [TestCase("Home and Garden", "genre-home-and-garden")]
    public void AGenresSlug_Should_BeLettersAndHyphens(string genre, string slug) =>
        DiscoverBuilder.GenreSlug(genre).Should().Be(slug);

    [Test]
    public void ThePageReads_TheFixedListsAndItsGenresLists()
    {
        DiscoverBuilder.KeysFor(Today, [Crime], Options).Should().BeEquivalentTo(
            "series-new-2026", "series-popular-2026", "movie-year-2026", "series-alltime", "movie-alltime",
            "series-genre-14", "movie-genre-14");
    }

    // ---- helpers --------------------------------------------------------------------

    private DiscoverPage Build(params DiscoverGenre[] genres) =>
        DiscoverBuilder.Build(new DiscoverInput(_lists, KnownTitles.None, genres), Today, Options);

    private static DiscoverRow Row(DiscoverPage page, string slug) => page.Rows.Single(r => r.Slug == slug);

    /// <summary>Every list of the page, each with 30 titles of its own.</summary>
    private void FillEveryList(IReadOnlyList<DiscoverGenre> genres)
    {
        DiscoverTitle[] Many(Func<int, DiscoverTitle> make) => Enumerable.Range(1, 30).Select(make).ToArray();

        Store("series-new-2026", Many(i => Series($"New {i}", aired: Today.AddDays(-i), score: 5000 - i)));
        Store("series-popular-2026", Many(i => Series($"Popular {i}", aired: new DateOnly(2026, 2, 1), score: 5000 - i)));
        Store("movie-year-2026", Many(i => Movie($"Movie {i}", year: 2026, score: 5000 - i)));
        Store("series-alltime", Many(i => Series($"All-time series {i}", year: 2000, score: 90_000 - i * 2)));
        Store("movie-alltime", Many(i => Movie($"All-time movie {i}", year: 1990, score: 90_001 - i * 2)));

        foreach (var genre in genres)
        {
            Store($"series-genre-{genre.Id}", Many(i => Series($"{genre.Name} series {i}", year: 2010 + i % 17, score: 5000 - i)));
            Store($"movie-genre-{genre.Id}", Many(i => Movie($"{genre.Name} movie {i}", year: 2000 + i % 27, score: 5000 - i)));
        }
    }

    private void Store(string key, params DiscoverTitle[] titles) => _lists[key] = new DiscoverList(key, titles, DateTime.UtcNow);

    private void Prepend(string key, DiscoverTitle title) =>
        _lists[key] = new DiscoverList(key, [title, .. _lists[key].Titles], DateTime.UtcNow);

    private DiscoverTitle Series(string name, DateOnly? aired = null, int? year = null, double score = 1000) => new()
    {
        Kind = DiscoverKind.Series, TvdbId = ++_nextId, Name = name, Year = year ?? aired?.Year ?? 2026,
        FirstAired = aired ?? (year is { } y ? new DateOnly(y, 1, 1) : null),
        ImageUrl = "https://artworks.thetvdb.com/p.jpg", Status = "Continuing", Score = score
    };

    private DiscoverTitle Movie(string name, int year, double score = 1000, string status = "Released") => new()
    {
        Kind = DiscoverKind.Movie, TvdbId = ++_nextId, Name = name, Year = year,
        ImageUrl = "https://artworks.thetvdb.com/m.jpg", Status = status, Score = score
    };
}
