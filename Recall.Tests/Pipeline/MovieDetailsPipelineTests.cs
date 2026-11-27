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

namespace Recall.Tests.Pipeline;

/// <summary>
/// Movie Details rendered by the real app, for a visitor and for a signed-in
/// user: the shared hero and action row (with the watchlist as a square
/// toggle), the section links, the facts that have a value, and the side
/// column. What each piece decides is tested on the page model
/// (<c>MovieDetailsGetTests</c>).
/// </summary>
[TestFixture]
public sealed class MovieDetailsPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const int MovieId = 79;

    private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        _baseFactory.TheTvDb
            .Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate
            {
                TvdbId = MovieId,
                Name = "Hero Test Movie",
                Slug = "hero-test-movie",
                Overview = "A movie only the pipeline tests will ever watch.",
                ImageUrl = "https://artworks.thetvdb.com/banners/movies/79/posters/1.jpg",
                BackgroundUrl = "https://artworks.thetvdb.com/banners/movies/79/backgrounds/1_t.jpg",
                ReleaseDate = new DateOnly(2020, 5, 1),
                RuntimeMinutes = 170,
                Budget = 60_000_000m,
                Status = new MovieStatus { Name = "Released" },
                Genres = ["Crime"],
                Studios = ["Warner Bros. Pictures"],
                Characters =
                [
                    new Character { Id = 1, PeopleId = 1, PersonName = "Ada Actor", Name = "The Lead", PeopleType = "Actor" },
                    new Character { Id = 2, PeopleId = 2, PersonName = "Dee Rector", PeopleType = "Director" }
                ]
            });

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
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "movie-user", Email = "movie@test.local" });
        await db.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Test]
    public async Task ASignedInUser_Should_GetTheHero_TheActionRow_AndTheWatchlistToggle()
    {
        var html = await PageAsync(signedIn: true);

        Regex.Matches(html, "<h1[ >]").Count.Should().Be(1);
        html.Should().MatchRegex("<img class=\"tvdb-hero__art\" src=\"[^\"]+backgrounds[^\"]+\" alt=\"\" fetchpriority=\"high\" loading=\"eager\"");
        html.Should().Contain("2020 · 2 h 50 min · Warner Bros. Pictures");
        html.Should().Contain(">Released<");
        html.Should().Contain("Mark as watched").And.Contain("tvdb-primary-action__detail\">2 h 50 min<");
        html.Should().MatchRegex("<button type=\"submit\" class=\"tvdb-square-button\" id=\"watchlistToggle\" aria-pressed=\"false\" aria-label=\"Add to watchlist\"");
        html.Should().Contain("aria-label=\"Like this movie\"").And.Contain("aria-label=\"Share Hero Test Movie\"");
        html.Should().NotContain("titleMenuButton", "nothing to unmark or remove: no \"⋯\" menu");
        html.Should().NotContain("tvdb-progress").And.NotContain("role=\"progressbar\"");
        html.Should().NotContain("tvdb-quiet-action").And.NotContain("tvdb-title__details");
    }

    [Test]
    public async Task ThePanel_Should_LinkItsSections_AndListOnlyTheFactsThatHaveAValue()
    {
        var html = await PageAsync(signedIn: false);

        html.Should().Contain("<a href=\"#overview\">Overview</a>").And.Contain("<a href=\"#cast\">Cast & crew</a>");
        html.Should().Contain("<dt>Release date</dt> <dd>May 1, 2020</dd>");
        html.Should().Contain("<dt>Runtime</dt> <dd>2 h 50 min</dd>");
        html.Should().Contain("<dt>Studio</dt> <dd>Warner Bros. Pictures</dd>");
        html.Should().Contain("<dt>Budget</dt> <dd class=\"tvdb-mono\">$60,000,000</dd>");
        html.Should().NotContain("<dt>Box office</dt>", "the movie has no box office figure");
        html.Should().NotContain("<dt>IMDb</dt>", "OMDb has nothing for it");
        html.Should().Contain("handler=ToggleWatchlist", "a visitor gets the same action row");

        // Overview, then cast, then crew: the one-column order below 992px, which
        // the stylesheet's grid areas rearrange from 992px (crew under the overview).
        var overview = html.IndexOf("id=\"overview\" class=\"tvdb-detail-section tvdb-movie-layout__overview\"", StringComparison.Ordinal);
        var cast = html.IndexOf("id=\"cast\" class=\"tvdb-detail-people tvdb-movie-layout__cast\"", StringComparison.Ordinal);
        var crew = html.IndexOf("id=\"crew\" class=\"tvdb-detail-people tvdb-movie-layout__crew\"", StringComparison.Ordinal);
        overview.Should().BePositive();
        cast.Should().BeGreaterThan(overview);
        crew.Should().BeGreaterThan(cast);
        html.Should().Contain("<section class=\"tvdb-cast \" data-cast data-collapsible> <div class=\"tvdb-cast__head\"> <h2 class=\"mb-0\">Crew", "the crew is the two-column list");
        html.Should().Contain("<section class=\"tvdb-cast tvdb-cast--column\" data-cast data-collapsible> <div class=\"tvdb-cast__head\"> <h2 class=\"mb-0\">Cast", "the cast is the one-column list");
        html.Should().Contain("Show all cast").And.Contain("Show all 1");
        html.Should().Contain("\"@type\":\"Movie\"", "the structured data is still there");
    }

    /// <summary>The page with its whitespace collapsed and its entities decoded ("·", "&amp;"), for plain matching.</summary>
    private async Task<string> PageAsync(bool signedIn)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/Movies/Details/{MovieId}");
        if (signedIn)
            request.Headers.Add(UserHeader, UserId.ToString());

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

            var principal = RecallPrincipal.Create(userId, "movie-user", "movie@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
