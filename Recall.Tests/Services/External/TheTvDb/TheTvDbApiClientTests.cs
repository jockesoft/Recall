using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
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

        return new TheTvDbApiClient(httpClient, tvdbState, logger.Object);
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