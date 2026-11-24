using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Tests.Pipeline;

/// <summary>
/// The Dashboard rendered by the real app for a signed-in user with three
/// series in progress: the header's stat links, "See all in Library", and
/// both layouts of Continue watching on the page (cards, and the phone's
/// compact rows), which the stylesheet chooses between; and for a second user
/// with eleven, where "Show all 11" replaces "See all in Library". What each
/// piece decides is tested on the page model (<c>DashboardModelTests</c>).
/// </summary>
[TestFixture]
public sealed class DashboardPipelineTests
{
    private const string UserHeader = "X-Test-User";

    private static readonly Guid UserId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly int[] SeriesIds = [91, 92, 93];

    private static readonly Guid ManyUserId = Guid.Parse("66666666-6666-6666-6666-666666666667");
    private static readonly int[] ManySeriesIds = Enumerable.Range(201, 11).ToArray();

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        foreach (var id in SeriesIds.Concat(ManySeriesIds))
        {
            _baseFactory.TheTvDb
                .Setup(x => x.GetSeriesAggregateByIdAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesAggregate
                {
                    TvdbId = id,
                    Name = $"Dashboard Show {id}",
                    Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                    Seasons = [new SeasonSummary { Id = id, Number = 8 }],
                    Episodes =
                    [
                        new EpisodeSummary { Id = id * 100 + 10, SeasonNumber = 8, EpisodeNumber = 10, Name = $"Episode of {id}", Aired = new DateOnly(2020, 1, 1), Image = $"https://artworks.thetvdb.com/banners/episodes/{id}.jpg" }
                    ]
                });
        }

        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName);
            }));

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "dash-user", Email = "dash@test.local" });
        db.AppUsers.Add(new AppUserEntity { Id = ManyUserId, Username = "dash-many", Email = "dash-many@test.local" });
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        foreach (var (user, id) in SeriesIds.Select(id => (UserId, id)).Concat(ManySeriesIds.Select(id => (ManyUserId, id))))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
                 VALUES ({Guid.NewGuid()}, {user}, {id}, {$"Dashboard Show {id}"}, {now.AddMinutes(-id)}, {now}, 1)
                 """);
        }
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Test]
    public async Task TheHeader_Should_LinkEachStat_ToWhatExplainsIt()
    {
        var html = await PageAsync();

        Regex.Matches(html, "<h1[ >]").Count.Should().Be(1);
        var stats = Regex.Match(html, "<ul class=\"tvdb-stat-row tvdb-dashboard-head__stats list-unstyled\">(.*?)</ul>").Groups[1].Value;
        Regex.Matches(stats, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .Should().Equal("/Library", "/Dashboard#upcoming", "/Library?section=watching");
        stats.Should().Contain("Tracked series").And.Contain("Airing this week").And.Contain("Unwatched episodes");
        // The phone's short label comes first, the full one hidden after it, so the
        // link's name starts with what a phone shows and keeps the full wording.
        stats.Should().Contain("<span class=\"tvdb-stat-item__short\">This week<span class=\"visually-hidden\">, Airing this week</span></span>");
        stats.Should().Contain("<span class=\"tvdb-stat-item__short\">Tracked<span class=\"visually-hidden\">, Tracked series</span></span>");
        stats.Should().Contain("<span class=\"tvdb-stat-item__short\">Unwatched<span class=\"visually-hidden\">, Unwatched episodes</span></span>");
    }

    [Test]
    public async Task ContinueWatching_Should_HaveBothLayouts_AndSeeAllInLibrary()
    {
        var html = await PageAsync();

        html.Should().Contain("<a class=\"tvdb-section-head__link tvdb-catchup-seeall-link\" id=\"continueWatchingSeeAll\" href=\"/Library?section=watching\">");
        html.Should().Contain("<a class=\"btn btn-outline-dark tvdb-catchup-seeall\" href=\"/Library?section=watching\">See all in Library</a>");

        html.Should().NotContain("data-catchup-toggle-form", "three series fit: no \"Show all\"");

        var grid = Regex.Match(html, "<div class=\"tvdb-catchup-grid\" data-collapsible-list>(.*?)<ul class=\"tvdb-catchup-rows").Groups[1].Value;
        Regex.Matches(grid, "class=\"tvdb-catchup-card\"").Count.Should().Be(3, "every series is a card (the stylesheet hides the others on a phone)");
        Regex.Matches(html, ">Up next<").Count.Should().Be(1, "only the first card carries it");
        grid.IndexOf(">Up next<", StringComparison.Ordinal).Should().BeLessThan(grid.IndexOf("class=\"tvdb-catchup-card\"", 30, StringComparison.Ordinal));

        var rows = Regex.Match(html, "<ul class=\"tvdb-catchup-rows list-unstyled\" aria-label=\"More to continue\" data-collapsible-list>(.*?)</ul> </div> <a class=\"btn").Groups[1].Value;
        Regex.Matches(rows, "class=\"tvdb-catchup-row\"").Count.Should().Be(2, "the first series is the card; the rest are rows");
        rows.Should().Contain("<span class=\"tvdb-mono\">S08E10</span>");
        rows.Should().Contain("class=\"tvdb-round-button tvdb-round-button--lg tvdb-round-button--on-paper\"").And.Contain("aria-label=\"Mark Episode of");
        rows.Should().MatchRegex("class=\"tvdb-round-button tvdb-round-button--lg tvdb-round-button--plain\" id=\"catchUpRowMenu\\d+\" data-bs-toggle=\"dropdown\" aria-expanded=\"false\" aria-label=\"More actions for Dashboard Show \\d+\"",
            "the row's \"⋯\" has no ring, a 44px target and its name");
        rows.Should().Contain("loading=\"lazy\"");
    }

    [Test]
    public async Task ContinueWatching_Should_RenderEverySeries_AndOfferShowAll_WhenThereAreMoreThanNine()
    {
        var html = await PageAsync(ManyUserId);

        html.Should().NotContain("continueWatchingSeeAll").And.NotContain("tvdb-catchup-seeall\"",
            "\"Show all 11\" replaces \"See all in Library\"");
        html.Should().Contain("<div class=\"tvdb-catchup \" id=\"continueWatchingList\" data-collapsible data-catchup>",
            "collapsed: the stylesheet shows the first nine");
        Regex.Matches(html, "class=\"tvdb-catchup-card\"").Count.Should().Be(11, "every series is rendered, hidden by count");
        Regex.Matches(html, "class=\"tvdb-catchup-row\"").Count.Should().Be(10);

        var toggle = Regex.Match(html, "<form method=\"get\" action=\"/Dashboard#continueWatching\" class=\"tvdb-catchup-toggle\" data-catchup-toggle-form>(.*?)</form>").Groups[1].Value;
        toggle.Should().Contain("<input type=\"hidden\" name=\"all\" value=\"true\" />", "without script it is a GET to ?all=true");
        toggle.Should().Contain("aria-expanded=\"false\" aria-controls=\"continueWatchingList\"");
        toggle.Should().MatchRegex("> Show all 11 </button>");

        Regex.Matches(html, "<input type=\"hidden\" name=\"all\" value=\"false\" data-catchup-all />").Count.Should().Be(42,
            "every mark and stop form of the 11 cards and 10 rows posts the state back");
        // Named by place, not by series: a mark returns to the same place in the list.
        html.Should().Contain("<li class=\"tvdb-catchup-row\" id=\"catchup-row-2\">")
            .And.Contain("<input type=\"hidden\" name=\"Anchor\" value=\"catchup-row-2\" />")
            .And.Contain("<div class=\"tvdb-catchup-card\" id=\"catchup-card-11\">")
            .And.NotContain("id=\"catchup-row-1\"", "the first place is the phone's card");
        Regex.Matches(html, "<input type=\"hidden\" name=\"last\" value=\"true\" />").Count.Should().Be(4,
            "only the last series' forms (its card's and its row's mark and stop) say it is the last");
    }

    [Test]
    public async Task ContinueWatching_Should_RenderExpanded_WithAllInTheAddress()
    {
        var html = await PageAsync(ManyUserId, "/Dashboard?all=true");

        html.Should().Contain("<div class=\"tvdb-catchup is-expanded\" id=\"continueWatchingList\" data-collapsible data-catchup>");
        var toggle = Regex.Match(html, "data-catchup-toggle-form>(.*?)</form>").Groups[1].Value;
        toggle.Should().NotContain("name=\"all\"", "\"Show fewer\" without script is a GET to the plain Dashboard");
        toggle.Should().Contain("aria-expanded=\"true\"").And.MatchRegex("> Show fewer </button>");
        Regex.Matches(html, "<input type=\"hidden\" name=\"all\" value=\"true\" data-catchup-all />").Count.Should().Be(42,
            "a mark from the expanded list comes back expanded");
        html.Should().Contain("<script src=\"/js/tvdb-dashboard.");
    }

    [Test]
    public async Task ContinueWatching_Should_KeepTheDormantLinkAndUpcoming_BelowTheWholeList()
    {
        var html = await PageAsync(ManyUserId);

        html.IndexOf("id=\"upcoming\"", StringComparison.Ordinal)
            .Should().BeGreaterThan(html.IndexOf("data-catchup-toggle-form", StringComparison.Ordinal));
        html.IndexOf("data-catchup-toggle-form", StringComparison.Ordinal)
            .Should().BeGreaterThan(html.LastIndexOf("class=\"tvdb-catchup-row\"", StringComparison.Ordinal));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ThePage_Should_HaveNoDuplicateIds_WithManySeries(bool expanded)
    {
        var html = await PageAsync(ManyUserId, expanded ? "/Dashboard?all=true" : "/Dashboard");

        Regex.Matches(html, " id=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty();
    }

    [Test]
    public async Task ThePage_Should_HaveNoDuplicateIds()
    {
        var html = await PageAsync();

        Regex.Matches(html, " id=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty("the card and the row of a series each have their own menu id");
    }

    /// <summary>The page with its whitespace collapsed and its entities decoded, for plain matching.</summary>
    private async Task<string> PageAsync(Guid? userId = null, string path = "/Dashboard")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(UserHeader, (userId ?? UserId).ToString());
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(Regex.Replace(await response.Content.ReadAsStringAsync(), @"\s+", " "));
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers[UserHeader], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = RecallPrincipal.Create(userId, "dash-user", "dash@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
