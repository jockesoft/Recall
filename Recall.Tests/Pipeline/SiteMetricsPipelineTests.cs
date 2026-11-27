using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Pipeline;

/// <summary>
/// The site metrics through the real pipeline: which requests count as views
/// and under what, that only fixed keys reach the table whatever the request
/// carried, and /Admin/Metrics (admins only, and its figures).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SiteMetricsPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const string RoleHeader = "X-Test-Role";
    private const string DistinctiveAgent = "Mozilla/5.0 (Distinctive-Browser-7f3a; rv:1.0) Gecko/20100101 Firefox/133.0";

    private static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName);
            }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = AdminId, Username = "metrics-admin", Email = "admin@test.local", Role = UserRole.Admin });
        await db.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [SetUp]
    public async Task ClearAsync()
    {
        // Start every test from nothing: drain what earlier requests counted, and empty the table.
        _factory.Services.GetRequiredService<SiteMetricsBuffer>().Drain();
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMetrics.ExecuteDeleteAsync();
    }

    private HttpClient Client(string? role = null, string userAgent = DistinctiveAgent)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(UserHeader, AdminId.ToString());
            client.DefaultRequestHeaders.Add(RoleHeader, role);
        }

        return client;
    }

    private Dictionary<string, long> Counted() =>
        _factory.Services.GetRequiredService<SiteMetricsBuffer>().Pending()
            .GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));

    [Test]
    public async Task PageViews_Should_BeCountedByPageType_AndWhetherTheVisitorIsSignedIn()
    {
        using var visitor = Client();
        using var user = Client(role: nameof(UserRole.User));

        (await visitor.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await visitor.GetAsync($"/Series/Details/{RecallWebApplicationFactory.KnownSeriesId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await visitor.GetAsync("/Account/Login?returnUrl=%2FSearch")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await visitor.GetAsync("/Privacy")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync($"/Series/Details/{RecallWebApplicationFactory.KnownSeriesId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        Counted().Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["view.landing.anon"] = 1,
            ["view.series.anon"] = 1,
            ["view.login.anon"] = 1,
            [SiteMetricKeys.SignInPageDirect] = 1,
            ["view.other.anon"] = 1,
            ["view.series.user"] = 1
        });
    }

    [Test]
    public async Task WhatIsNotAPageView_Should_NotBeCounted()
    {
        using var visitor = Client();

        await visitor.GetAsync("/css/tokens.css");
        await visitor.GetAsync("/health/live");
        await visitor.GetAsync("/sitemap.xml");
        await visitor.GetAsync("/no-such-page");
        await visitor.GetAsync("/Dashboard");      // a redirect to sign in

        Counted().Should().BeEmpty();
    }

    [Test]
    public async Task ACrawlersViews_Should_BeCountedOnTheirOwn_AndNotAsVisitors()
    {
        using var googlebot = Client(userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        await googlebot.GetAsync("/");
        await googlebot.GetAsync($"/Series/Details/{RecallWebApplicationFactory.KnownSeriesId}");

        Counted().Should().Equal(new Dictionary<string, long> { [SiteMetricKeys.CrawlerView] = 2 });
    }

    [Test]
    public async Task NothingAboutTheVisitor_Should_ReachTheTable()
    {
        using var visitor = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Series/Details/{RecallWebApplicationFactory.KnownSeriesId}?utm_source=mail-7f3a");
        request.Headers.Add("X-Forwarded-For", "203.0.113.77");
        await visitor.SendAsync(request);
        await visitor.GetAsync("/Account/Login?returnUrl=%2FSeries%2FDetails%2F42");

        await _factory.Services.GetRequiredService<SiteMetricsFlusher>().FlushAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMetrics.AsNoTracking().ToListAsync();

        rows.Should().NotBeEmpty();
        foreach (var row in rows)
        {
            SiteMetricKeys.All.Should().Contain(row.Key);
            row.Key.Should().NotContainAny("203.0.113", "Distinctive", "7f3a", "utm", "/", "42");
        }
    }

    [Test]
    public async Task ThePrivacyPage_Should_SayWhatIsCounted()
    {
        using var visitor = Client();

        var html = await visitor.GetStringAsync("/Privacy");

        html.Should().Contain("Recall counts daily page visits and sign-ins as anonymous totals, with no cookies and nothing about who visited.");
        html.Should().Contain("The anonymous daily totals of visits and sign-ins: deleted 400 days after the day they count.");
        html.Should().Contain("<code>Recall.Pending</code>").And.Contain("after 15 minutes", "the action cookie is listed with the others");
    }

    // ---- /Admin/Metrics ----------------------------------------------------------------

    [Test]
    public async Task TheMetricsPage_Should_SendAVisitorToSignIn()
    {
        using var visitor = Client();

        var response = await visitor.GetAsync("/Admin/Metrics");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Account/Login");
    }

    [Test]
    public async Task TheMetricsPage_Should_BeClosedToAUserWhoIsNotAnAdmin()
    {
        using var user = Client(role: nameof(UserRole.User));

        var response = await user.GetAsync("/Admin/Metrics");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Sign-up funnel");
    }

    [Test]
    public async Task TheMetricsPage_Should_ShowTheTotals_AndTheFunnel_ToAnAdmin()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteMetrics.AddRange(
                new SiteMetricDailyEntity { Date = today, Key = "view.series.anon", Count = 1200 },
                new SiteMetricDailyEntity { Date = today.AddDays(-10), Key = "view.series.anon", Count = 800 },
                new SiteMetricDailyEntity { Date = today, Key = "signin.page.direct", Count = 30 },
                new SiteMetricDailyEntity { Date = today, Key = "signin.link-sent.direct", Count = 12 },
                new SiteMetricDailyEntity { Date = today, Key = "signin.signed-in.direct", Count = 6 },
                new SiteMetricDailyEntity { Date = today, Key = "signin.new-account.direct", Count = 3 },
                new SiteMetricDailyEntity { Date = today, Key = "action.clicked.add-series", Count = 8 },
                new SiteMetricDailyEntity { Date = today, Key = "action.completed.add-series", Count = 2 });
            await db.SaveChangesAsync();
        }

        using var admin = Client(role: nameof(UserRole.Admin));
        var response = await admin.GetAsync("/Admin/Metrics");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("Sign-up funnel");
        html.Should().Contain("1,200").And.Contain("2,000", "the 30-day total of visitors' series views");
        html.Should().Contain("Action path").And.Contain("Direct path");
        html.Should().Contain(">40%<", "12 emails of 30 direct sign-in page views");
        html.Should().Contain(">50%<", "6 sign-ins of 12 emails, and 3 new accounts of 6 sign-ins");
        System.Text.RegularExpressions.Regex.Matches(html, @">(\d+)%<").Select(m => int.Parse(m.Groups[1].Value))
            .Should().NotBeEmpty().And.OnlyContain(percent => percent <= 100, "no step shows more than all of the one before");
        System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ")
            .Should().Contain("<th scope=\"row\">Add a series to the library</th> <td class=\"text-end tvdb-mono\">8</td> <td class=\"text-end tvdb-mono\">2</td>",
                "one row per action, pressed then done");
        html.Should().Contain("Last 30 days");
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers[UserHeader], out var userId)
                || !Enum.TryParse<UserRole>(Request.Headers[RoleHeader], out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = RecallPrincipal.Create(userId, "metrics-admin", "admin@test.local", role);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
