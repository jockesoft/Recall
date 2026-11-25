using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Filter;
using Recall.Web.Mappings;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services.External.TheTvDb;

/// <summary>
/// TheTVDB's filter endpoints, as Discover's daily job uses them: one request
/// for one page of 500 base records, English-original titles from every
/// country, and the records mapped to <see cref="DiscoverTitle"/>. The JSON
/// below is the shape of real responses (2026-11-25), trimmed.
/// </summary>
[TestFixture]
public sealed class TheTvDbFilterTests
{
    [Test]
    public void TheSeriesPath_Should_AskForEnglishOriginals_FromEveryCountry_HighestScoreFirst()
    {
        new TitleFilter(TitleFilterSort.Score, Year: 2026).SeriesPath()
            .Should().Be("series/filter?lang=eng&sort=score&sortType=desc&year=2026");
        new TitleFilter(TitleFilterSort.FirstAired, Year: 2026).SeriesPath()
            .Should().Be("series/filter?lang=eng&sort=firstAired&sortType=desc&year=2026");
        new TitleFilter(TitleFilterSort.Score, GenreId: 14).SeriesPath()
            .Should().Be("series/filter?lang=eng&sort=score&sortType=desc&genre=14");
        new TitleFilter(TitleFilterSort.Score).SeriesPath()
            .Should().NotContain("country").And.NotContain("page", "Discover only ever reads the first page");
    }

    [Test]
    public void TheMoviePath_Should_HaveNoSortType_AndRefuseADateOrder()
    {
        new TitleFilter(TitleFilterSort.Score, Year: 2026, GenreId: 2).MoviesPath()
            .Should().Be("movies/filter?lang=eng&sort=score&year=2026&genre=2");

        var byDate = () => new TitleFilter(TitleFilterSort.FirstAired).MoviesPath();
        byDate.Should().Throw<InvalidOperationException>("the movie filter does not order by date");
    }

    [Test]
    public async Task FilterSeries_Should_SendOneRequest_AndReadTheRecords()
    {
        var api = Api(SeriesPage);
        var client = CreateSut(api);

        using var meter = TheTvDbRequestMeter.Start();
        var records = await client.FilterSeriesAsync(new TitleFilter(TitleFilterSort.Score, Year: 2026));

        meter.Count.Should().Be(2, "the login and the one filter request");
        api.Requests.Select(r => r.Uri!.PathAndQuery).Last()
            .Should().Be("/v4/series/filter?lang=eng&sort=score&sortType=desc&year=2026");

        records.Should().HaveCount(3);
        records[0].Id.Should().Be(445000);
        records[0].Status!.Name.Should().Be("Continuing");
    }

    [Test]
    public async Task FilterMovies_Should_ReadTheRecords()
    {
        var client = CreateSut(Api(MoviePage));

        var records = await client.FilterMoviesAsync(new TitleFilter(TitleFilterSort.Score, Year: 2026));

        records.Should().ContainSingle().Which.Image.Should().Be("/banners/v4/movie/338552/posters/1.jpg");
    }

    [Test]
    public async Task AFailedFilterRequest_Should_Throw()
    {
        var client = CreateSut(new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Json(Login)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") }));

        var act = () => client.FilterSeriesAsync(new TitleFilter(TitleFilterSort.Score));

        await act.Should().ThrowAsync<TheTvDbApiException>();
    }

    [Test]
    public void ASeriesRecord_Should_MapToADiscoverTitle()
    {
        var title = new FilterRecordDto
        {
            Id = 445000, Name = " VisionQuest ", Image = "https://artworks.thetvdb.com/banners/v4/series/445000/posters/1.jpg",
            Year = "2026", FirstAired = "2026-10-13", Score = 11858, Status = new() { Name = "Continuing" }
        }.ToDiscoverTitle(DiscoverKind.Series);

        title.Should().BeEquivalentTo(new DiscoverTitle
        {
            Kind = DiscoverKind.Series, TvdbId = 445000, Name = "VisionQuest", Year = 2026,
            ImageUrl = "https://artworks.thetvdb.com/banners/v4/series/445000/posters/1.jpg",
            FirstAired = new DateOnly(2026, 10, 13), Status = "Continuing", Score = 11858
        });
    }

    [Test]
    public void AMovieRecord_Should_GetAnAbsolutePoster_AndNoDate()
    {
        var title = new FilterRecordDto
        {
            Id = 338552, Name = "Avengers: Doomsday", Image = "/banners/v4/movie/338552/posters/1.jpg",
            Year = "2026", Score = 69813, Status = new() { Name = "Released" }
        }.ToDiscoverTitle(DiscoverKind.Movie)!;

        title.ImageUrl.Should().Be("https://artworks.thetvdb.com/banners/v4/movie/338552/posters/1.jpg");
        title.FirstAired.Should().BeNull();
        title.Kind.Should().Be(DiscoverKind.Movie);
    }

    [TestCase(null, null, null)]
    [TestCase("", "", null)]
    [TestCase("not a year", "2026-13-40", null)]
    public void UnusableYearsAndDates_Should_BeLeftOut(string? year, string? firstAired, int? expectedYear)
    {
        var title = new FilterRecordDto { Id = 1, Name = "Something", Year = year, FirstAired = firstAired }
            .ToDiscoverTitle(DiscoverKind.Series)!;

        title.Year.Should().Be(expectedYear);
        title.FirstAired.Should().BeNull();
        title.Score.Should().Be(0, "a missing score counts as none");
    }

    [Test]
    public void ARecordWithoutAName_Should_NotMap()
    {
        new FilterRecordDto { Id = 1, Name = "  " }.ToDiscoverTitle(DiscoverKind.Series).Should().BeNull();
        new FilterRecordDto { Id = 0, Name = "No id" }.ToDiscoverTitle(DiscoverKind.Movie).Should().BeNull();
    }

    private const string Login = """{"status":"success","data":{"token":"test-token"}}""";

    private const string SeriesPage = """
        {"status":"success","data":[
          {"id":445000,"name":"VisionQuest","image":"https://artworks.thetvdb.com/x.jpg","year":"2026","firstAired":"2026-10-13",
           "lastAired":"2026-12-01","nextAired":"","score":11858,"status":{"id":1,"name":"Continuing","recordType":"series","keepUpdated":false},
           "originalCountry":"usa","originalLanguage":"eng","overview":"…","nameTranslations":null,"overviewTranslations":null},
          {"id":415556,"name":"Genesis: A Living Conversation","image":"https://artworks.thetvdb.com/y.jpg","year":"9696","firstAired":"9696-01-01","score":7,
           "status":{"id":3,"name":"Upcoming"}},
          {"id":483593,"name":"Crazy Rich Asians","image":null,"year":"2028","firstAired":"2028-01-01","score":0,"status":{"id":3,"name":"Upcoming"}}
        ],"links":{"prev":null,"self":"…","next":"…?page=1","total_items":1529,"page_size":500}}
        """;

    private const string MoviePage = """
        {"status":"success","data":[
          {"id":338552,"name":"Avengers: Doomsday","slug":"avengers-doomsday","image":"/banners/v4/movie/338552/posters/1.jpg",
           "nameTranslations":null,"overviewTranslations":null,"aliases":null,"score":69813,"runtime":150,
           "status":{"id":3,"name":"Filming / Post-Production","recordType":"movie","keepUpdated":true},"lastUpdated":"2026-11-01 10:00:00","year":"2026"}
        ],"links":{"prev":null,"self":"…","next":"…","total_items":1239,"page_size":500}}
        """;

    private static StubHttpMessageHandler Api(string page) =>
        new(request => request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal) ? Json(Login) : Json(page));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TheTvDbApiClient CreateSut(HttpMessageHandler handler)
    {
        var options = Options.Create(new TheTvDbOptions { BaseUrl = "https://api4.thetvdb.com/v4/", ApiKey = "unit-test-api-key" });

        return new TheTvDbApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") },
            new TheTvDbClientState(options, NullLogger<TheTvDbClientState>.Instance),
            Polly.ResiliencePipeline<HttpResponseMessage>.Empty,
            NullLogger<TheTvDbApiClient>.Instance);
    }
}
