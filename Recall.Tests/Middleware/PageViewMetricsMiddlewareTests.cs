using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Tests.TestSupport;
using Recall.Web.Middleware;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Middleware;

[TestFixture]
public sealed class PageViewMetricsMiddlewareTests
{
    private const string Chrome =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string Googlebot =
        "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    private RecordingSiteMetrics _metrics = null!;

    [SetUp]
    public void SetUp() => _metrics = new RecordingSiteMetrics();

    private async Task RunAsync(
        string? page, string method = "GET", int status = 200, string contentType = "text/html; charset=utf-8",
        bool signedIn = false, string userAgent = Chrome)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers.UserAgent = userAgent;
        if (signedIn)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], "Test"));

        if (page is not null)
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new PageActionDescriptor { ViewEnginePath = page }), page));

        var middleware = new PageViewMetricsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            return Task.CompletedTask;
        }, _metrics);

        await middleware.InvokeAsync(context);
    }

    [TestCase("/Index", "view.landing.anon")]
    [TestCase("/Series/Details", "view.series.anon")]
    [TestCase("/Movies/Details", "view.movie.anon")]
    [TestCase("/Episodes/Details", "view.episode.anon")]
    [TestCase("/Discover", "view.discover.anon")]
    [TestCase("/Search", "view.search.anon")]
    [TestCase("/Account/Login", "view.login.anon")]
    [TestCase("/Privacy", "view.other.anon")]
    public async Task AVisitorsView_Should_BeCountedUnderItsPageType(string page, string key)
    {
        await RunAsync(page);

        _metrics.Keys.Should().Equal(key);
    }

    [Test]
    public async Task ASignedInView_Should_BeCountedApart()
    {
        await RunAsync("/Series/Details", signedIn: true);

        _metrics.Keys.Should().Equal("view.series.user");
    }

    [Test]
    public async Task ACrawlersView_Should_BeCountedOnlyAsACrawlersView()
    {
        await RunAsync("/Series/Details", userAgent: Googlebot);

        _metrics.Keys.Should().Equal(SiteMetricKeys.CrawlerView);
    }

    [TestCase("POST", 200, "text/html")]
    [TestCase("GET", 302, "text/html")]
    [TestCase("GET", 404, "text/html")]
    [TestCase("GET", 200, "application/json")]
    [TestCase("GET", 200, "application/xml")]
    [TestCase("HEAD", 200, "text/html")]
    public async Task WhatIsNotAPageView_Should_NotBeCounted(string method, int status, string contentType)
    {
        await RunAsync("/Series/Details", method, status, contentType);

        _metrics.Keys.Should().BeEmpty();
    }

    [Test]
    public async Task ARequestWithoutAPage_Should_NotBeCounted()
    {
        await RunAsync(page: null);

        _metrics.Keys.Should().BeEmpty("a static file or a health check is not a page");
    }
}
