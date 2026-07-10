using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Services.External.TheTvDb;
using AwesomeAssertions;

namespace Recall.Tests.Services.External.TheTvDb;

[TestFixture]
public class TheTvDbApiClientTests
{
    [Test]
    public async Task SearchSeriesAsync_Should_Login_Then_ReturnResults()
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
            JsonResponse(HttpStatusCode.OK, """
            {
              "status":"success",
              "data":[
                { "tvdb_id": 123, "name":"Dark", "type":"series", "year":"2017" }
              ]
            }
            """)
        }));

        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.SearchSeriesAsync("dark");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].TvdbId.Should().Be(123);
        result[0].Name.Should().Be("Dark");
    }

    [Test]
    public async Task GetSeriesByIdAsync_Should_Login_Then_MapSeriesResponse()
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
            JsonResponse(HttpStatusCode.OK, """
            {
              "status":"success",
              "data": {
                "id": 10,
                "name":"Breaking Bad",
                "slug":"breaking-bad",
                "overview":"overview",
                "image":"https://example.com/image.jpg",
                "firstAired":"2008-01-20",
                "score":9.5
              }
            }
            """)
        }));

        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.GetSeriesByIdAsync(10);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(10);
        result.Name.Should().Be("Breaking Bad");
        result.Slug.Should().Be("breaking-bad");
        result.FirstAired.Should().Be("2008-01-20");
        result.Score.Should().Be(9.5);
    }

    [Test]
    public void GetSeriesByIdAsync_Should_ThrowArgumentOutOfRange_WhenIdIsInvalid()
    {
        // Arrange
        var handlerMock = CreateHandlerMock(new Queue<HttpResponseMessage>());
        var sut = CreateSut(handlerMock.Object);

        // Act
        Func<Task> act = async () => await sut.GetSeriesByIdAsync(0);

        // Assert
        act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SearchSeriesAsync_Should_ThrowTheTvDbApiException_WhenLoginFails()
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
        Func<Task> act = async () => await sut.SearchSeriesAsync("dark");

        // Assert
        act.Should().ThrowAsync<TheTvDbApiException>()
            .WithMessage("*login failed*");
    }

    [Test]
    public void SearchSeriesAsync_Should_ThrowTheTvDbApiException_WhenSearchFails()
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
        Func<Task> act = async () => await sut.SearchSeriesAsync("dark");

        // Assert
        act.Should().ThrowAsync<TheTvDbApiException>()
            .WithMessage("*request failed*");
    }

    [Test]
    public async Task SearchSeriesAsync_Should_ReturnEmpty_WhenQueryIsWhitespace_AndNotCallHttp()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var sut = CreateSut(handlerMock.Object);

        // Act
        var result = await sut.SearchSeriesAsync("   ");

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
    public async Task Should_LoginOnlyOnce_ForMultipleCalls()
    {
        // Arrange
        var callCount = 0;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                callCount++;

                if (request.RequestUri!.AbsolutePath.EndsWith("/login"))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "token":"test-token" }
                    }
                    """);
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("/search"))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data":[]
                    }
                    """);
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("/series/1"))
                {
                    return JsonResponse(HttpStatusCode.OK, """
                    {
                      "status":"success",
                      "data": { "id":1, "name":"Series 1" }
                    }
                    """);
                }

                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            });

        var sut = CreateSut(handlerMock.Object);

        // Act
        await sut.SearchSeriesAsync("x");
        await sut.GetSeriesByIdAsync(1);

        // Assert
        callCount.Should().Be(3); // login + search + series/1
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

        var statelogger = new Mock<ILogger<TheTvDbClientState>>();
        var tvdbState = new TheTvDbClientState(options, statelogger.Object);
        
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

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}