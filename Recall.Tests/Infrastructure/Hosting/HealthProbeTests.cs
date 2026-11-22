using System.Net;
using AwesomeAssertions;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Hosting;

namespace Recall.Tests.Infrastructure.Hosting;

/// <summary>
/// The container healthcheck (<c>dotnet Recall.Web.dll healthcheck</c>): which
/// port it asks, and what exit code each answer gives.
/// </summary>
[TestFixture]
public sealed class HealthProbeTests
{
    // ---- the port -------------------------------------------------------------------

    [TestCase("http://+:8701", null, 8701, TestName = "production: URLS with a wildcard host")]
    [TestCase("http://+:8701", "8080", 8701, TestName = "URLS wins over the image's HTTP_PORTS, as in Kestrel")]
    [TestCase("http://*:5000", null, 5000)]
    [TestCase("http://0.0.0.0:7000", null, 7000)]
    [TestCase("http://[::]:8701", null, 8701, TestName = "an IPv6 host")]
    [TestCase("http://localhost:5164/base/path", null, 5164, TestName = "a path after the port")]
    [TestCase("http://localhost", null, 80, TestName = "no port is http's 80")]
    [TestCase("HTTP://+:8701", null, 8701, TestName = "the scheme in any case")]
    [TestCase("https://localhost:7123;http://localhost:5164", null, 5164, TestName = "the first http address of several")]
    [TestCase(" http://+:8701 ; http://+:9000 ", null, 8701, TestName = "spaces around entries")]
    [TestCase("https://+:443", "8701", 8701, TestName = "https-only URLS falls through to HTTP_PORTS")]
    [TestCase(null, "8701", 8701, TestName = "HTTP_PORTS alone")]
    [TestCase(null, "8701;8702", 8701, TestName = "the first of several HTTP_PORTS")]
    [TestCase("", "", 8080, TestName = "both empty: the default")]
    [TestCase(null, null, 8080, TestName = "neither set: the default")]
    [TestCase("http://+:notaport", "eighty", 8080, TestName = "unusable values: the default")]
    [TestCase("http://+:70000", null, 8080, TestName = "out-of-range port: the default")]
    public void PortFrom_Should_PickThePortKestrelListensOn(string? urls, string? httpPorts, int expected)
    {
        HealthProbe.PortFrom(urls, httpPorts).Should().Be(expected);
    }

    // ---- the exit code --------------------------------------------------------------

    [TestCase(200, 0)]
    [TestCase(204, 0)]
    [TestCase(299, 0)]
    [TestCase(301, 1, TestName = "a redirect is not followed and is not healthy")]
    [TestCase(404, 1)]
    [TestCase(429, 1)]
    [TestCase(500, 1)]
    [TestCase(503, 1)]
    public void ExitCodeFor_Should_BeZeroOnlyForSuccess(int status, int expected)
    {
        HealthProbe.ExitCodeFor(status).Should().Be(expected);
    }

    [Test]
    public async Task RunAsync_Should_AskTheLiveEndpointOnLoopback_AndExitZeroWhenItAnswers200()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var output = new StringWriter();

        var code = await HealthProbe.RunAsync(8701, handler, output: output);

        code.Should().Be(0);
        handler.Requests.Should().ContainSingle()
            .Which.Should().Be((HttpMethod.Get, new Uri("http://127.0.0.1:8701/health/live"), (string?)null));
        output.ToString().Should().StartWith("healthy:");
    }

    [Test]
    public async Task RunAsync_Should_ExitOne_WhenTheAppAnswersAnError()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var output = new StringWriter();

        (await HealthProbe.RunAsync(8701, handler, output: output)).Should().Be(1);
        output.ToString().Should().Contain("unhealthy").And.Contain("503");
    }

    [Test]
    public async Task RunAsync_Should_ExitOne_WhenNothingListens()
    {
        var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));
        var output = new StringWriter();

        (await HealthProbe.RunAsync(8701, handler, output: output)).Should().Be(1);
        output.ToString().Should().Contain("did not answer").And.Contain("Connection refused");
    }

    [Test]
    public async Task RunAsync_Should_ExitOne_WhenTheAppDoesNotAnswerInTime()
    {
        var output = new StringWriter();

        var code = await HealthProbe.RunAsync(8701, new HangingHandler(), timeout: TimeSpan.FromMilliseconds(50), output: output);

        code.Should().Be(1);
        output.ToString().Should().Contain("did not answer");
    }

    [Test]
    public void Timeout_Should_BeThreeSeconds()
    {
        HealthProbe.Timeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    /// <summary>Never answers; only the client's timeout ends the request.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
