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

    // ---- the buttons, and what they do ----------------------------------------------

    [Test]
    public async Task ACard_Should_CarryItsAddAndNotInterestedButtons()
    {
        var html = await PageAsync("/Discover");
        var series = Card(html, "discover-new-1");
        var movie = Card(html, "discover-movies-1");

        series.Should().Contain("<form method=\"post\" action=\"/Discover?handler=Hide\">");
        series.Should().Contain("title=\"Not interested\" aria-label=\"Not interested in New series 1\"");
        series.Should().Contain("<form method=\"post\" action=\"/Discover?handler=Add\">");
        series.Should().Contain("title=\"Add to library\" aria-label=\"Add New series 1 to your library\"");
        series.Should().Contain("<input type=\"hidden\" name=\"kind\" value=\"Series\" />")
            .And.Contain("<input type=\"hidden\" name=\"tvdbId\" value=\"1001\" />")
            .And.Contain("<input type=\"hidden\" name=\"Anchor\" value=\"discover-new-1\" />")
            .And.NotContain("name=\"last\"");

        movie.Should().Contain("title=\"Add to watchlist\" aria-label=\"Add Movie 1 to your watchlist\"");
        movie.Should().Contain("tvdb-icon-badge", "the bookmark with a plus");
        Card(html, "discover-new-20").Should().Contain("<input type=\"hidden\" name=\"last\" value=\"true\" />");

        html.Should().Contain("Add a series to your library").And.Contain("Add a movie to your watchlist")
            .And.Contain("Not interested: hide it from Discover");
    }

    [Test]
    public async Task NotInterested_Should_HideTheTitleFromEveryRow_UntilUndone()
    {
        var (factory, client) = await NewUserAsync();
        using var _ = factory;
        using var __ = client;

        var page = await PageAsync(client, "/Discover");
        Card(page, "discover-new-1").Should().Contain("value=\"1001\"");

        var hide = await PostFormAsync(client, Card(page, "discover-new-1"), "Hide");
        hide.StatusCode.Should().Be(HttpStatusCode.Redirect);
        hide.Headers.Location!.ToString().Should().Be("/Discover#discover-new-1", "back to the same place");

        var after = await PageAsync(client, "/Discover");
        after.Should().Contain("Hidden from Discover.");
        after.Should().NotContain("href=\"/Series/Details/1001\"", "a hidden title is in no row");
        Card(after, "discover-new-1").Should().Contain("value=\"1002\"", "the next title has the place");

        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().DiscoverDismissals
                .Count(d => d.TvdbId == 1001 && d.Kind == DiscoverKind.Series).Should().Be(1);

        var undoForm = Regex.Match(after, "<form[^>]*handler=Undo[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        undoForm.Should().Contain("name=\"action\" value=\"Hide\"");
        var undo = await PostFormAsync(client, undoForm, "Undo");
        undo.Headers.Location!.ToString().Should().Be("/Discover#discover-new-1");

        var undone = await PageAsync(client, "/Discover");
        undone.Should().Contain("New series 1 is back in Discover.");
        Card(undone, "discover-new-1").Should().Contain("value=\"1001\"", "the title is back at its place");
    }

    [Test]
    public async Task AddingAMovie_Should_PutItOnTheWatchlist_WithoutAskingTheTvdb()
    {
        var (factory, client) = await NewUserAsync();
        using var _ = factory;
        using var __ = client;
        _baseFactory.TheTvDbApi.Invocations.Clear();

        var page = await PageAsync(client, "/Discover");
        var add = await PostFormAsync(client, Card(page, "discover-movies-1"), "Add");

        add.Headers.Location!.ToString().Should().Be("/Discover#discover-movies-1");
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().TrackedMovies
                .Single().Should().Match<TrackedMovieEntity>(m => m.TvdbId == 5001 && m.Name == "Movie 1");

        var after = await PageAsync(client, "/Discover");
        after.Should().Contain("Added Movie 1 to your watchlist.");
        after.Should().NotContain("href=\"/Movies/Details/5001\"", "a title on the watchlist is no longer suggested");
        _baseFactory.TheTvDbApi.Invocations.Should().BeEmpty();
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

    /// <summary>A new user with nothing watched, signed in on a client of their own (their own cookies).</summary>
    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> NewUserAsync()
    {
        var userId = Guid.NewGuid();
        var factory = SignedIn(_baseFactory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppUsers.Add(new AppUserEntity { Id = userId, Username = $"u{userId:N}", Email = $"{userId:N}@test.local" });
            await db.SaveChangesAsync();
        }

        return (factory, Client(factory, userId));
    }

    /// <summary>One card's markup, from its opening tag to the next card (or the end of its row).</summary>
    private static string Card(string html, string id) =>
        Regex.Match(html, $"<div class=\"tvdb-series-card-wrap tvdb-discover-card\" id=\"{id}\".*?(?=<div class=\"tvdb-series-card-wrap|</section>)",
            RegexOptions.Singleline).Value;

    /// <summary>Submits the form of <paramref name="handler"/> found in <paramref name="html"/>, with its hidden fields and token.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string html, string handler)
    {
        var form = Regex.Match(html, $"<form method=\"post\"[^>]*? action=\"([^\"]*handler={handler})\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
        form.Success.Should().BeTrue($"the {handler} form is there");

        var fields = Regex.Matches(form.Groups[2].Value, "<input (?:type=\"hidden\" )?name=\"([^\"]+)\" (?:type=\"hidden\" )?value=\"([^\"]*)\"")
            .ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        fields.Should().ContainKey("__RequestVerificationToken");

        return await client.PostAsync(WebUtility.HtmlDecode(form.Groups[1].Value), new FormUrlEncodedContent(fields));
    }

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(Regex.Replace(await response.Content.ReadAsStringAsync(), @"\s+", " "));
    }

    private Task<string> PageAsync(string path) => PageAsync(_client, path);

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
