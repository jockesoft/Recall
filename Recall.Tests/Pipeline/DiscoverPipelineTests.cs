using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Pipeline;

/// <summary>
/// Discover rendered by the real app, with both TheTVDB mocks strict and only
/// the cache reads Stats makes set up: the page shows the user's rows from
/// the stored lists and makes no TheTVDB request (a call nobody set up would
/// throw and fail the page). Signed-in only, not indexed, in the navigation.
/// </summary>
[TestFixture]
public sealed class DiscoverPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private static readonly Guid UserId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly int Year = Today.Year;

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory(strictTheTvDb: true);
        _factory = SignedIn(_baseFactory);
        _client = Client(_factory, UserId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "discover-user", Email = "discover@test.local" });

        // Five crime series watched: enough titles with genres for genre rows, Crime first.
        for (var series = 501; series <= 505; series++)
        {
            db.EpisodeWatches.Add(new EpisodeWatchEntity
            {
                Id = Guid.NewGuid(), UserId = UserId, SeriesTvdbId = series, EpisodeTvdbId = series * 10,
                WatchedUtc = DateTime.UtcNow.AddDays(-series % 7), Source = WatchSource.Single
            });

            var id = series;
            _baseFactory.TheTvDb
                .Setup(x => x.GetCachedSeriesAggregateAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesAggregate
                {
                    TvdbId = id, Name = $"Watched crime {id}", Genres = ["Crime"], AverageRuntimeMinutes = 50,
                    Episodes = [new EpisodeSummary { Id = id * 10, SeasonNumber = 1, EpisodeNumber = 1, RuntimeMinutes = 50 }]
                });
        }

        await db.SaveChangesAsync();

        var lists = scope.ServiceProvider.GetRequiredService<IDiscoverListRepository>();
        await lists.SaveAsync($"series-new-{Year}", Many(1000, i => Series(1000 + i, $"New series {i}", Today.AddDays(-i))), DateTime.UtcNow);
        await lists.SaveAsync($"series-popular-{Year}", Many(2000, i => Series(2000 + i, $"Popular series {i}", new DateOnly(Year, 1, 1))), DateTime.UtcNow);
        await lists.SaveAsync("series-genre-14", Many(3000, i => Series(3000 + i, $"Crime series {i}", new DateOnly(2015, 1, 1))), DateTime.UtcNow);
        await lists.SaveAsync("movie-genre-14", Many(4000, i => Movie(4000 + i, $"Crime movie {i}", 2024)), DateTime.UtcNow);
        await lists.SaveAsync($"movie-year-{Year}", Many(5000, i => Movie(5000 + i, $"Movie {i}", Year)), DateTime.UtcNow);
        // A list holding a title the user watched: it must not be suggested.
        await lists.SaveAsync("series-alltime", [Series(501, "Watched crime 501", new DateOnly(2010, 1, 1))], DateTime.UtcNow);
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Test]
    public async Task ThePage_Should_ShowTheUsersRows_InOrder()
    {
        var html = await PageAsync("/Discover");

        Regex.Matches(html, "<h2 id=\"discover-[a-z-]+-heading\">([^<]+)</h2>").Select(m => m.Groups[1].Value)
            .Should().Equal("New series", $"Popular from {Year}", "Popular in Crime", $"Movies from {Year}");
        html.Should().Contain("<p class=\"tvdb-meta\">Premiered in the last 90 days, the most popular first.</p>");
        html.Should().Contain("Picked from TheTVDB and updated daily. Anything already in your library, on your watchlist or watched is left out.");
        html.Should().NotContain("All-time favourites", "the user has genres");
        html.Should().NotContain("Watched crime", "a title the user watched is never suggested");
    }

    [Test]
    public async Task ACard_Should_LinkToItsPage_AndBeNamedByItsPlace()
    {
        var html = await PageAsync("/Discover");

        html.Should().Contain("<div class=\"tvdb-series-card-wrap tvdb-discover-card\" id=\"discover-new-1\" data-content-type=\"series\">");
        html.Should().Contain("href=\"/Series/Details/1001\"");
        html.Should().Contain($"<span class=\"tvdb-series-year\">Series · {Today.AddDays(-1).Year}</span>");
        html.Should().Contain("id=\"discover-movies-1\" data-content-type=\"movie\"");
        html.Should().Contain("href=\"/Movies/Details/5001\"");
        html.Should().Contain($"<span class=\"tvdb-series-year\">Movie · {Year}</span>");
        html.Should().Contain("width=\"150\" height=\"225\" loading=\"eager\"", "the first posters of the first row load at once");
        html.Should().Contain("loading=\"lazy\"");
    }

    [Test]
    public async Task ThePage_Should_MakeNoTheTvdbRequest()
    {
        _baseFactory.TheTvDb.Invocations.Clear();
        _baseFactory.TheTvDbApi.Invocations.Clear();

        await PageAsync("/Discover");

        _baseFactory.TheTvDbApi.Invocations.Should().BeEmpty("the transport client is never touched");
        _baseFactory.TheTvDb.Invocations.Select(i => i.Method.Name).Distinct()
            .Should().BeEquivalentTo(["GetCachedSeriesAggregateAsync"], "only Stats' cache reads, which never reach TheTVDB");
    }

    [Test]
    public async Task ThePage_Should_NotBeIndexed_AndBeInTheNavigation()
    {
        var html = await PageAsync("/Discover");

        html.Should().Contain("<meta name=\"robots\" content=\"noindex, nofollow\" />");
        html.Should().Contain("<a class=\"nav-link\" aria-current=\"page\" href=\"/Discover\">Discover</a>");
        html.IndexOf("href=\"/Discover\"", StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf("href=\"/Search\"", StringComparison.Ordinal), "Discover sits between Home and Search");
    }

    [Test]
    public async Task ThePage_Should_HaveNoDuplicateIds()
    {
        var html = await PageAsync("/Discover");

        Regex.Matches(html, " id=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty();
    }

    [Test]
    public async Task AVisitor_Should_BeSentToSignIn()
    {
        using var anonymous = _baseFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });

        var response = await anonymous.GetAsync("/Discover");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Account/Login");
    }

    [Test]
    public async Task BeforeTheFirstFetch_Should_SaySuggestionsAreOnTheirWay()
    {
        using var empty = new RecallWebApplicationFactory(strictTheTvDb: true);
        using var factory = SignedIn(empty);
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppUsers.Add(new AppUserEntity { Id = userId, Username = "new-user", Email = "new@test.local" });
            await db.SaveChangesAsync();
        }

        using var client = Client(factory, userId);
        var response = await client.GetAsync("/Discover");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Suggestions are on their way");
        html.Should().NotContain("tvdb-discover-row");
    }

    // ---- helpers --------------------------------------------------------------------

    private static IReadOnlyList<DiscoverTitle> Many(int _, Func<int, DiscoverTitle> make) =>
        Enumerable.Range(1, 25).Select(make).ToList();

    private static DiscoverTitle Series(int id, string name, DateOnly aired) => new()
    {
        Kind = DiscoverKind.Series, TvdbId = id, Name = name, Year = aired.Year, FirstAired = aired,
        ImageUrl = $"https://artworks.thetvdb.com/banners/{id}.jpg", Status = "Continuing", Score = 10_000 - id % 1000
    };

    private static DiscoverTitle Movie(int id, string name, int year) => new()
    {
        Kind = DiscoverKind.Movie, TvdbId = id, Name = name, Year = year,
        ImageUrl = $"https://artworks.thetvdb.com/banners/{id}.jpg", Status = "Released", Score = 10_000 - id % 1000
    };

    private async Task<string> PageAsync(string path)
    {
        var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(Regex.Replace(await response.Content.ReadAsStringAsync(), @"\s+", " "));
    }

    private static WebApplicationFactory<Program> SignedIn(RecallWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName);
            }));

    private static HttpClient Client(WebApplicationFactory<Program> factory, Guid userId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(UserHeader, userId.ToString());
        return client;
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

            var principal = RecallPrincipal.Create(userId, "discover-user", "discover@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
