using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Services.External.TheTvDb;
using AwesomeAssertions;
using System.Text.Json;

namespace Recall.Tests.Services.External.TheTvDb;

[TestFixture]
public class TheTvDbApiClientTests
{
    [Test]
    public void SeriesDataDto_Should_Deserialize_Lists_Genres_RemoteIds_Overview_And_Companies()
    {
        // Arrange
        const string json = """
        {
          "status": "success",
          "data": {
            "id": 42,
            "name": "Example Series",
            "overview": "Series overview text.",
            "image": "/banners/series/42.jpg",
            "isOrderRandomized": true,
            "lastAired": "2024-10-01",
            "lastUpdated": "2024-10-05",
            "nameTranslations": ["eng"],
            "companies": [
              {
                "activeDate": "2020-01-01",
                "aliases": [
                  {
                    "language": "eng",
                    "name": "Example Co Alias"
                  }
                ],
                "country": "us",
                "id": 7,
                "inactiveDate": null,
                "name": "Example Company",
                "nameTranslations": ["eng"],
                "overviewTranslations": ["eng"],
                "primaryCompanyType": 1,
                "slug": "example-company",
                "parentCompany": {
                  "id": 8,
                  "name": "Parent Company",
                  "relation": {
                    "id": 9,
                    "typeName": "parent"
                  }
                },
                "tagOptions": [
                  {
                    "helpText": "help",
                    "id": 10,
                    "name": "tag-name",
                    "tag": 11,
                    "tagName": "tag-group"
                  }
                ]
              }
            ],
            "genres": [
              {
                "id": 12,
                "name": "Drama",
                "slug": "drama"
              }
            ],
            "remoteIds": [
              {
                "id": "tt1234567",
                "type": 2,
                "sourceName": "IMDB"
              }
            ],
            "lists": [
              {
                "aliases": [
                  {
                    "language": "eng",
                    "name": "Prestige TV"
                  }
                ],
                "id": 13,
                "image": "/banners/lists/13.jpg",
                "imageIsFallback": true,
                "isOfficial": true,
                "name": "Top Lists",
                "nameTranslations": ["eng"],
                "overview": "List overview",
                "overviewTranslations": ["eng"],
                "remoteIds": [
                  {
                    "id": "list-remote-1",
                    "type": 3,
                    "sourceName": "TVDB"
                  }
                ],
                "tags": [
                  {
                    "helpText": "tag help",
                    "id": 14,
                    "name": "featured",
                    "tag": 15,
                    "tagName": "curation"
                  }
                ],
                "score": 8.9,
                "url": "https://example.test/lists/13"
              }
            ]
          }
        }
        """;

        // Act
        var envelope = JsonSerializer.Deserialize<TheTvDbEnvelopeDto<SeriesDataDto>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // Assert
        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();

        var dto = envelope.Data!;
        dto.Id.Should().Be(42);
        dto.Overview.Should().Be("Series overview text.");
        dto.Companies.Should().ContainSingle();
        dto.Companies![0].Name.Should().Be("Example Company");
        dto.Companies[0].ParentCompany!.Name.Should().Be("Parent Company");
        dto.Companies[0].TagOptions.Should().ContainSingle();

        dto.Genres.Should().ContainSingle();
        dto.Genres![0].Name.Should().Be("Drama");
        dto.Genres[0].Slug.Should().Be("drama");

        dto.RemoteIds.Should().ContainSingle();
        dto.RemoteIds![0].Id.Should().Be("tt1234567");
        dto.RemoteIds[0].SourceName.Should().Be("IMDB");

        dto.Lists.Should().ContainSingle();
        dto.Lists![0].Id.Should().Be(13);
        dto.Lists[0].IsOfficial.Should().BeTrue();
        dto.Lists[0].ImageIsFallback.Should().BeTrue();
        dto.Lists[0].Score.Should().Be(8.9);
        dto.Lists[0].RemoteIds.Should().ContainSingle();
        dto.Lists[0].RemoteIds![0].Id.Should().Be("list-remote-1");
        dto.Lists[0].Tags.Should().ContainSingle();
        dto.Lists[0].Tags![0].Name.Should().Be("featured");
    }

    [Test]
    public async Task SearchAsync_Should_Login_Then_ReturnResults()
    {
        // Arrange
        var handlerMock = CreateHandlerMock(new Queue<HttpResponseMessage>([
            JsonResponse(HttpStatusCode.OK, """
                                            {
                                              "status":"success",
                                              "data": { "token":"test-token" }
                                            }
                                            """),
            JsonResponse(HttpStatusCode.OK, """
            {
              "status":"success",
              "data":[
                { "tvdb_id": 123, "name":"Dark", "type":"series", "year":"2017" }
              ]
            }
            """)
        ]));

        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.SearchAsync("dark");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].TvdbId.Should().Be(123);
        result[0].Name.Should().Be("Dark");
    }

    [Test]
    public void SearchAsync_Should_ThrowTheTvDbApiException_WhenLoginFails()
    {
        // Arrange
        var handlerMock = CreateHandlerMock(new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse(HttpStatusCode.Unauthorized, """
            {
              "status":"failure",
              "message":"Unauthorized"
            }
            """)
        }));

        var sut = CreateSut(handlerMock.Object);

        // Act
        Func<Task> act = async () => await sut.SearchAsync("dark");

        // Assert
        act.Should().ThrowAsync<TheTvDbApiException>()
            .WithMessage("*login failed*");
    }

    [Test]
    public void SearchAsync_Should_ThrowTheTvDbApiException_WhenSearchFails()
    {
        // Arrange
        var handlerMock = CreateHandlerMock(new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse(HttpStatusCode.OK, """
            {
              "status":"success",
              "data": { "token":"test-token" }
            }
            """),
            JsonResponse(HttpStatusCode.InternalServerError, """
            {
              "status":"failure",
              "message":"Server error"
            }
            """)
        }));

        var sut = CreateSut(handlerMock.Object);

        // Act
        Func<Task> act = async () => await sut.SearchAsync("dark");

        // Assert
        act.Should().ThrowAsync<TheTvDbApiException>()
            .WithMessage("*request failed*");
    }

    [Test]
    public async Task SearchAsync_Should_ReturnEmpty_WhenQueryIsWhitespace_AndNotCallHttp()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.SearchAsync("   ");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_ReturnAggregate_WhenTranslationFetchFails()
    {
        // Arrange: the series call succeeds but the (best-effort) translation call
        // fails — the aggregate must still come back rather than being discarded.
        var handlerMock = CreateRoutedHandlerMock(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"token":"test-token"}}""");

            if (path.Contains("/translations/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.InternalServerError, """{"status":"failure","message":"Server error"}""");

            if (path.Contains("/episodes/default/eng", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"id":42,"episodes":[]},"links":{"next":null}}""");

            if (path.Contains("/extended", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "id": 42, "name": "Dark", "episodes": [] }
                    }
                    """);

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.GetSeriesAggregateByIdAsync(42);

        // Assert
        result.Should().NotBeNull("a failed translation fetch must not discard an already-successful series fetch");
        result!.TvdbId.Should().Be(42);
        result.Name.Should().Be("Dark");
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_StopPaging_WhenLinksNextNeverBecomesNull()
    {
        // TheTVDB's own pagination metadata could, in principle, get stuck with
        // "next" always populated (has happened with other third-party paginated
        // APIs) — the season-episode loop must still terminate instead of paging
        // forever.
        var episodeId = 0;
        var handlerMock = CreateRoutedHandlerMock(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"token":"test-token"}}""");

            if (path.Contains("/translations/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.NotFound, """{"status":"failure","message":"no translation"}""");

            if (path.Contains("/extended", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "id": 7, "name": "Stuck", "episodes": [], "seasons": [{ "number": 1 }] }
                    }
                    """);

            // The translated list is not available either, so the per-season listing is the last resort.
            if (path.Contains("/episodes/default/eng", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.InternalServerError, """{"status":"failure","message":"Server error"}""");

            if (path.Contains("/episodes/default", StringComparison.Ordinal))
            {
                var id = ++episodeId;
                return JsonResponse(HttpStatusCode.OK,
                    "{\"status\":\"success\",\"data\":{\"episodes\":[{\"id\":" + id +
                    ",\"seasonNumber\":1,\"number\":" + id + "}],\"links\":{\"next\":\"always-more\"}}}");
            }

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.GetSeriesAggregateByIdAsync(7);

        // Assert
        result.Should().NotBeNull();
        result!.Episodes.Should().HaveCount(50, "the pagination loop must stop at its safety cap instead of running forever");
    }

    [Test]
    public async Task GetMovieAggregateByIdAsync_Should_ReturnAggregate_WhenTranslationFetchFails()
    {
        // Arrange: the movie call succeeds but the (best-effort) translation call
        // fails — the aggregate must still come back rather than being discarded.
        var handlerMock = CreateRoutedHandlerMock(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"token":"test-token"}}""");

            if (path.Contains("/translations/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.InternalServerError, """{"status":"failure","message":"Server error"}""");

            if (path.Contains("/extended", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "id": 287533, "name": "Oppenheimer" }
                    }
                    """);

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.GetMovieAggregateByIdAsync(287533);

        // Assert
        result.Should().NotBeNull("a failed translation fetch must not discard an already-successful movie fetch");
        result!.TvdbId.Should().Be(287533);
        result.Name.Should().Be("Oppenheimer");
    }

    [Test]
    public async Task GetMovieAggregateByIdAsync_Should_UseTranslatedNameAndOverview_WhenTranslationSucceeds()
    {
        var handlerMock = CreateRoutedHandlerMock(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"token":"test-token"}}""");

            if (path.Contains("/translations/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "name": "Oppenheimer", "overview": "The story of J. Robert Oppenheimer.", "language": "eng" }
                    }
                    """);

            if (path.Contains("/extended", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "id": 287533, "name": "Oppenheimer (raw)" }
                    }
                    """);

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        var sut = CreateSut(handlerMock.Object);

        var result = await sut.GetMovieAggregateByIdAsync(287533);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Oppenheimer");
        result.Overview.Should().Be("The story of J. Robert Oppenheimer.");
    }

    [Test]
    public async Task GetMovieAggregateByIdAsync_Should_ReturnNull_WhenMovieNotFound()
    {
        var handlerMock = CreateRoutedHandlerMock(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"token":"test-token"}}""");

            if (path.Contains("/translations/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.NotFound, """{"status":"failure","message":"not found"}""");

            if (path.Contains("/extended", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data": null}""");

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        var sut = CreateSut(handlerMock.Object);

        var result = await sut.GetMovieAggregateByIdAsync(999999999);

        result.Should().BeNull();
    }

    // ---- what a series costs: a fixed few requests, never one per episode ----------

    private const string Login = """{"status":"success","data":{"token":"test-token"}}""";

    /// <summary>A TheTVDB that knows one series; <paramref name="translatedPage"/> answers the translated episode list by page number.</summary>
    private static StubHttpMessageHandler SeriesApi(string extended, Func<int, HttpResponseMessage> translatedPage) =>
        new(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, Login);
            if (path.EndsWith("/series/334824/translations/eng", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"status":"success","data":{"name":"Dark","overview":"A missing child."}}""");
            if (path.EndsWith("/series/334824/extended", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, extended);
            if (path.EndsWith("/series/334824/episodes/default/eng", StringComparison.Ordinal))
                return translatedPage(int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["page"]!));

            // Anything else, an episode's own endpoints included, is a request the series fetch must not make.
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

    private static IEnumerable<string> PathsOf(StubHttpMessageHandler api) =>
        api.Requests.Select(r => r.Uri!.PathAndQuery.Replace("/v4/", ""));

    private const string DarkExtended = """
        {
          "status":"success",
          "data": {
            "id": 334824, "name": "Dark", "overview": "Nach dem Verschwinden eines Kindes.", "averageRuntime": 56,
            "genres": [{ "id": 1, "name": "Drama" }],
            "seasons": [{ "id": 1, "number": 0 }, { "id": 2, "number": 1 }],
            "episodes": [
              { "id": 10, "seasonNumber": 1, "number": 1, "name": "Geheimnisse", "overview": "Ein Junge verschwindet.",
                "aired": "2017-12-01", "runtime": 51, "image": "/banners/episodes/10.jpg", "isMovie": 0, "finaleType": null },
              { "id": 11, "seasonNumber": 1, "number": 2, "name": "Lügen", "overview": "Die Polizei ist ratlos.",
                "aired": "2017-12-01", "runtime": 44, "image": null, "isMovie": 0, "finaleType": "season" },
              { "id": 12, "seasonNumber": 1, "number": 3, "name": "Gestern und Heute", "overview": "Es ist 1986.",
                "aired": "2017-12-01", "runtime": 45, "isMovie": 0 },
              { "id": 5, "seasonNumber": 0, "number": 1, "name": "Hinter den Kulissen", "overview": null,
                "aired": "2018-01-01", "runtime": 20, "isMovie": 1 }
            ]
          }
        }
        """;

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_TranslateEpisodesFromTheEpisodeList_InThreeRequests()
    {
        // The translated list: English for 10; a name but no overview for 11; nothing at all for 12; 5 is missing from it.
        var api = SeriesApi(DarkExtended, _ => JsonResponse(HttpStatusCode.OK, """
            {
              "status":"success",
              "data": { "id": 334824, "name": "Dark", "episodes": [
                { "id": 10, "seasonNumber": 1, "number": 1, "name": "Secrets", "overview": "A boy disappears." },
                { "id": 11, "seasonNumber": 1, "number": 2, "name": "Lies", "overview": null },
                { "id": 12, "seasonNumber": 1, "number": 3, "name": null, "overview": "" }
              ] },
              "links": { "prev": null, "self": "page=0", "next": null, "total_items": 3, "page_size": 500 }
            }
            """));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        PathsOf(api).Should().BeEquivalentTo(
        [
            "login",
            "series/334824/extended?meta=episodes&short=false",
            "series/334824/translations/eng",
            "series/334824/episodes/default/eng?page=0"
        ]);

        // Every field the cached aggregate had before, with the same fallbacks
        // the per-episode translation gave: a missing translation keeps the original.
        aggregate!.Name.Should().Be("Dark");
        aggregate.Overview.Should().Be("A missing child.");
        aggregate.AverageRuntimeMinutes.Should().Be(56);
        aggregate.Genres.Should().Equal("Drama");
        aggregate.Seasons.Select(s => s.Number).Should().Equal(0, 1);

        var byId = aggregate.Episodes.ToDictionary(e => e.Id);
        byId.Keys.Should().BeEquivalentTo([10, 11, 12, 5]);

        byId[10].Should().BeEquivalentTo(new Recall.Web.Domain.TheTvDb.EpisodeSummary
        {
            Id = 10, SeasonNumber = 1, EpisodeNumber = 1, Name = "Secrets", Overview = "A boy disappears.",
            Aired = new DateOnly(2017, 12, 1), RuntimeMinutes = 51,
            Image = "https://artworks.thetvdb.com/banners/episodes/10.jpg", IsMovie = false, FinaleType = null
        });
        byId[11].Should().BeEquivalentTo(new Recall.Web.Domain.TheTvDb.EpisodeSummary
        {
            Id = 11, SeasonNumber = 1, EpisodeNumber = 2, Name = "Lies", Overview = "Die Polizei ist ratlos.",
            Aired = new DateOnly(2017, 12, 1), RuntimeMinutes = 44, Image = null, IsMovie = false, FinaleType = "season"
        });
        byId[12].Name.Should().Be("Gestern und Heute", "a blank translation keeps the original name");
        byId[12].Overview.Should().Be("Es ist 1986.");
        byId[5].Name.Should().Be("Hinter den Kulissen", "an episode the translated list lacks keeps the original");
        byId[5].IsMovie.Should().BeTrue();
        byId[5].SeasonNumber.Should().Be(0);
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_CostTheSame_HoweverManyEpisodes_OnePagePerFiveHundred()
    {
        static string Episodes(int from, int count) => string.Join(",", Enumerable.Range(from, count).Select(n =>
            "{\"id\":" + n + ",\"seasonNumber\":1,\"number\":" + n + ",\"name\":\"Episode " + n + "\"}"));

        var extended = "{\"status\":\"success\",\"data\":{\"id\":334824,\"name\":\"Long\",\"episodes\":["
                       + Episodes(1, 600).Replace("Episode", "Folge") + "]}}";

        var api = SeriesApi(extended, page => JsonResponse(HttpStatusCode.OK,
            "{\"status\":\"success\",\"data\":{\"id\":334824,\"episodes\":["
            + (page == 0 ? Episodes(1, 500) : Episodes(501, 100))
            + "]},\"links\":{\"next\":" + (page == 0 ? "\"page=1\"" : "null") + "}}"));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        aggregate!.Episodes.Should().HaveCount(600);
        aggregate.Episodes.Should().OnlyContain(e => e.Name.StartsWith("Episode "), "all six hundred are translated");

        PathsOf(api).Should().BeEquivalentTo(
        [
            "login",
            "series/334824/extended?meta=episodes&short=false",
            "series/334824/translations/eng",
            "series/334824/episodes/default/eng?page=0",
            "series/334824/episodes/default/eng?page=1"
        ], "600 episodes cost two pages, not 600 translation requests");
        PathsOf(api).Should().NotContain(path => path.StartsWith("episodes/"), "no request per episode, ever");
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_KeepOriginalEpisodeNames_WhenTheTranslatedListFails_WithoutAskingPerEpisode()
    {
        var api = SeriesApi(DarkExtended, _ => JsonResponse(HttpStatusCode.InternalServerError, """{"status":"failure"}"""));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        aggregate!.Episodes.Single(e => e.Id == 10).Name.Should().Be("Geheimnisse");
        api.Requests.Should().HaveCount(4, "login, the record, the series translation and the one failed list request");
        PathsOf(api).Should().NotContain(path => path.StartsWith("episodes/"));
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_UseTheTranslatedList_WhenTheExtendedRecordHasNoEpisodes()
    {
        var api = SeriesApi(
            """{"status":"success","data":{"id":334824,"name":"Dark","seasons":[{"id":2,"number":1}],"episodes":[]}}""",
            _ => JsonResponse(HttpStatusCode.OK, """
                {"status":"success","data":{"id":334824,"episodes":[
                  {"id":10,"seasonNumber":1,"number":1,"name":"Secrets","aired":"2017-12-01","runtime":51}
                ]},"links":{"next":null}}
                """));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        aggregate!.Episodes.Should().ContainSingle().Which.Name.Should().Be("Secrets");
        aggregate.Episodes[0].RuntimeMinutes.Should().Be(51);
        api.Requests.Should().HaveCount(4, "the per-season listing is not needed: the translated list has the episodes");
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_KeepEveryStill_ThroughTheTranslatedList_WithNormalizedPaths()
    {
        // 10 has a relative path in both lists, 11 an absolute one, 12 a still
        // only the translated list knows about, and 5 has none anywhere.
        var api = SeriesApi(DarkExtended.Replace(
                "\"runtime\": 44, \"image\": null", "\"runtime\": 44, \"image\": \"https://artworks.thetvdb.com/banners/v4/episode/11/screencap/abc.jpg\""),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "status":"success",
                  "data": { "id": 334824, "episodes": [
                    { "id": 10, "seasonNumber": 1, "number": 1, "name": "Secrets", "image": "/banners/episodes/10.jpg" },
                    { "id": 11, "seasonNumber": 1, "number": 2, "name": "Lies", "image": null },
                    { "id": 12, "seasonNumber": 1, "number": 3, "name": "Past and Present", "image": "/banners/episodes/12.jpg" }
                  ] },
                  "links": { "next": null }
                }
                """));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        var stills = aggregate!.Episodes.ToDictionary(e => e.Id, e => e.Image);
        stills[10].Should().Be("https://artworks.thetvdb.com/banners/episodes/10.jpg", "a relative path is made absolute");
        stills[11].Should().Be("https://artworks.thetvdb.com/banners/v4/episode/11/screencap/abc.jpg",
            "the extended record's still survives a translated entry that has none");
        stills[12].Should().Be("https://artworks.thetvdb.com/banners/episodes/12.jpg");
        stills[5].Should().BeNull("TheTVDB has no still for it");
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_NormalizeStills_WhenTheEpisodesComeFromTheTranslatedListAlone()
    {
        var api = SeriesApi(
            """{"status":"success","data":{"id":334824,"name":"Dark","episodes":[]}}""",
            _ => JsonResponse(HttpStatusCode.OK, """
                {"status":"success","data":{"id":334824,"episodes":[
                  {"id":10,"seasonNumber":1,"number":1,"name":"Secrets","image":"/banners/episodes/10.jpg"}
                ]},"links":{"next":null}}
                """));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        aggregate!.Episodes.Single().Image.Should().Be("https://artworks.thetvdb.com/banners/episodes/10.jpg");
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_StopPagingTheTranslatedList_WhenNextNeverBecomesNull()
    {
        var api = SeriesApi(DarkExtended, page => JsonResponse(HttpStatusCode.OK,
            "{\"status\":\"success\",\"data\":{\"id\":334824,\"episodes\":[{\"id\":" + (1000 + page)
            + ",\"seasonNumber\":9,\"number\":" + page + "}]},\"links\":{\"next\":\"always-more\"}}"));

        var aggregate = await CreateSut(api).GetSeriesAggregateByIdAsync(334824);

        aggregate!.Episodes.Should().HaveCount(4, "the episodes are the extended record's");
        PathsOf(api).Count(path => path.Contains("/episodes/default/eng")).Should().Be(20, "the safety cap");
    }

    [Test]
    public async Task TheRequestMeter_Should_CountEveryRequestInsideIt_LoginIncluded_AndNothingOutside()
    {
        var api = SeriesApi(DarkExtended, _ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"id":334824,"episodes":[]},"links":{"next":null}}"""));
        var sut = CreateSut(api);

        int inner;
        using (var outer = TheTvDbRequestMeter.Start())
        {
            using (var meter = TheTvDbRequestMeter.Start())
            {
                await sut.GetSeriesAggregateByIdAsync(334824);
                inner = meter.Count;
            }

            await sut.GetSeriesAggregateByIdAsync(334824);   // the token is cached now: three requests

            inner.Should().Be(4, "login, extended, translation, the episode list");
            outer.Count.Should().Be(7, "an inner meter's requests count in the outer one too");
        }

        using var after = TheTvDbRequestMeter.Start();
        after.Count.Should().Be(0);
        api.Requests.Should().HaveCount(7);
    }

    private static TheTvDbApiClient CreateSut(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api4.thetvdb.com/v4/")
        };

        var options = Options.Create(new TheTvDbOptions
        {
            BaseUrl = "https://api4.thetvdb.com/v4/",
            ApiKey = "unit-test-api-key",
            Pin = "1234"
        });

        var stateLogger = new Mock<ILogger<TheTvDbClientState>>();
        var tvdbState = new TheTvDbClientState(options, stateLogger.Object);

        var logger = new Mock<ILogger<TheTvDbApiClient>>();

        return new TheTvDbApiClient(
            httpClient, tvdbState, Polly.ResiliencePipeline<HttpResponseMessage>.Empty, logger.Object);
    }

    private static Mock<HttpMessageHandler> CreateHandlerMock(Queue<HttpResponseMessage> responses)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                if (responses.Count == 0)
                    throw new InvalidOperationException("No more mocked HTTP responses queued.");

                return responses.Dequeue();
            });

        return handlerMock;
    }

    private static Mock<HttpMessageHandler> CreateRoutedHandlerMock(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) => responder(request));

        return handlerMock;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}