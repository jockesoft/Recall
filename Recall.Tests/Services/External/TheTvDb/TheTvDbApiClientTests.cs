using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Services.External.TheTvDb;
using AwesomeAssertions;
using Recall.Web.Infrastructure.Caching;

namespace Recall.Tests.Services.External.TheTvDb;

[TestFixture]
public class TheTvDbApiClientTests
{
    [Test]
    public async Task SearchSeriesAsync_Should_Login_Then_ReturnResults()
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
        var result = await sut.SearchSeriesAsync("dark");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].TvdbId.Should().Be(123);
        result[0].Name.Should().Be("Dark");
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

    private static TheTvDbApiClient CreateSut(HttpMessageHandler handler, Mock<IDistributedCacheJson>? cacheMock = null)
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
        cacheMock ??= new Mock<IDistributedCacheJson>();

        return new TheTvDbApiClient(httpClient, tvdbState, logger.Object, cacheMock.Object);
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