using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using AwesomeAssertions;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services.External.TheTvDb;

[TestFixture]
public class TheTvDbClientStateTests
{
    [Test]
    public async Task GetOrRefreshTokenAsync_Should_ReturnCachedToken_WithoutLoggingInAgain_WhenNotStale()
    {
        var loginCount = 0;
        var sut = CreateSut(() => $"token-{++loginCount}", out var httpClient);

        var first = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: null, CancellationToken.None);
        var second = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: null, CancellationToken.None);

        first.Should().Be("token-1");
        second.Should().Be("token-1");
        loginCount.Should().Be(1, "the second call didn't flag any token as stale, so the cached one should be reused");
    }

    [Test]
    public async Task GetOrRefreshTokenAsync_Should_LoginAgain_WhenStaleTokenMatchesCurrentToken()
    {
        var loginCount = 0;
        var sut = CreateSut(() => $"token-{++loginCount}", out var httpClient);

        var first = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: null, CancellationToken.None);
        var second = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: first, CancellationToken.None);

        second.Should().Be("token-2");
        loginCount.Should().Be(2, "the caller reported the cached token as rejected, so a fresh login is required");
    }

    [Test]
    public async Task GetOrRefreshTokenAsync_Should_NotLoginAgain_WhenStaleTokenWasAlreadyReplaced()
    {
        // Simulates two concurrent 401s racing to refresh the same expired
        // token: the first one to get the lock logs in and replaces it; the
        // second must notice its "stale" token no longer matches the cache and
        // just reuse the fresh one instead of logging in a second time.
        var loginCount = 0;
        var sut = CreateSut(() => $"token-{++loginCount}", out var httpClient);

        var originalToken = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: null, CancellationToken.None);

        // First 401-triggered refresh wins the race and replaces the token.
        var refreshed = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: originalToken, CancellationToken.None);
        refreshed.Should().Be("token-2");

        // A second caller that also observed the now-stale `originalToken`
        // (e.g. a concurrent request that got its own 401 before the refresh
        // above completed) must not trigger a third login.
        var second = await sut.GetOrRefreshTokenAsync(httpClient, staleToken: originalToken, CancellationToken.None);

        second.Should().Be("token-2", "the token was already refreshed by someone else");
        loginCount.Should().Be(2, "the second caller's stale token no longer matches the cache, so it must not log in again");
    }

    private static TheTvDbClientState CreateSut(Func<string> nextToken, out HttpClient httpClient)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"success\",\"data\":{\"token\":\"" + nextToken() + "\"}}",
                    Encoding.UTF8,
                    "application/json")
            });

        httpClient = new HttpClient(handlerMock.Object) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") };

        var options = Options.Create(new TheTvDbOptions { ApiKey = "unit-test-api-key" });
        return new TheTvDbClientState(options, new Mock<ILogger<TheTvDbClientState>>().Object);
    }
}
