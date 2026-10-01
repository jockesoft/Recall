using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Services.Authentication;

namespace Recall.Tests.Services.Authentication;

[TestFixture]
public class TurnstileVerifierTests
{
    private static readonly TurnstileOptions Enabled = new() { SiteKey = "site-key", SecretKey = "secret-key" };

    private static TurnstileVerifier Create(StubHttpMessageHandler handler, TurnstileOptions options) =>
        new(new HttpClient(handler), Options.Create(options), NullLogger<TurnstileVerifier>.Instance);

    // ---- disabled ----------------------------------------------------------------

    [TestCase(null, null)]
    [TestCase("site-key", null)]
    [TestCase(null, "secret-key")]
    [TestCase("site-key", " ")]
    public async Task Disabled_Should_PassEveryone_WithoutCallingCloudflare(string? siteKey, string? secretKey)
    {
        var handler = StubHttpMessageHandler.Json("""{"success":false}""");
        var sut = Create(handler, new TurnstileOptions { SiteKey = siteKey, SecretKey = secretKey });

        (await sut.VerifyAsync(token: null, remoteIp: "198.51.100.9")).Should().BeTrue(
            "with either key missing the CAPTCHA is switched off, and the sign-in form must still work");
        handler.Requests.Should().BeEmpty();
    }

    // ---- enabled -----------------------------------------------------------------

    [Test]
    public async Task Enabled_Should_Pass_WhenCloudflareConfirmsTheToken()
    {
        var handler = StubHttpMessageHandler.Json("""{"success":true,"error-codes":[]}""");
        var sut = Create(handler, Enabled);

        (await sut.VerifyAsync("the-token", "198.51.100.9")).Should().BeTrue();

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.ToString().Should().Be("https://challenges.cloudflare.com/turnstile/v0/siteverify");
        request.Body.Should().Contain("secret=secret-key").And.Contain("response=the-token").And.Contain("remoteip=198.51.100.9");
    }

    [Test]
    public async Task Enabled_Should_LeaveOutTheRemoteIp_WhenThereIsNone()
    {
        var handler = StubHttpMessageHandler.Json("""{"success":true}""");
        var sut = Create(handler, Enabled);

        await sut.VerifyAsync("the-token", remoteIp: null);

        handler.Requests.Single().Body.Should().NotContain("remoteip");
    }

    [Test]
    public async Task Enabled_Should_Fail_WhenCloudflareRejectsTheToken()
    {
        var handler = StubHttpMessageHandler.Json("""{"success":false,"error-codes":["invalid-input-response"]}""");
        var sut = Create(handler, Enabled);

        (await sut.VerifyAsync("a-forged-token", "198.51.100.9")).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Enabled_Should_Fail_WithoutCallingCloudflare_WhenNoTokenWasSubmitted(string? token)
    {
        var handler = StubHttpMessageHandler.Json("""{"success":true}""");
        var sut = Create(handler, Enabled);

        (await sut.VerifyAsync(token, "198.51.100.9")).Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    // ---- failures fail closed --------------------------------------------------------

    [Test]
    public async Task NetworkFailure_Should_FailClosed()
    {
        var sut = Create(StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused")), Enabled);

        (await sut.VerifyAsync("the-token", "198.51.100.9")).Should().BeFalse(
            "Cloudflare being unreachable must not become a way around the CAPTCHA");
    }

    [Test]
    public async Task Timeout_Should_FailClosed()
    {
        var sut = Create(StubHttpMessageHandler.Throwing(new TaskCanceledException("timed out", new TimeoutException())), Enabled);

        (await sut.VerifyAsync("the-token", "198.51.100.9")).Should().BeFalse();
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task AnErrorStatus_Should_FailClosed(HttpStatusCode status)
    {
        var sut = Create(StubHttpMessageHandler.Json("""{"success":true}""", status), Enabled);

        (await sut.VerifyAsync("the-token", "198.51.100.9")).Should().BeFalse("the body is not to be trusted on an error response");
    }

    [Test]
    public async Task AnUnreadableBody_Should_FailClosed()
    {
        var sut = Create(StubHttpMessageHandler.Json("<html>not json</html>"), Enabled);

        (await sut.VerifyAsync("the-token", "198.51.100.9")).Should().BeFalse();
    }

    [Test]
    public async Task CallerCancellation_Should_Propagate_RatherThanCountAsAFailedChallenge()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = Create(StubHttpMessageHandler.Throwing(new OperationCanceledException(cts.Token)), Enabled);

        var act = () => sut.VerifyAsync("the-token", "198.51.100.9", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
