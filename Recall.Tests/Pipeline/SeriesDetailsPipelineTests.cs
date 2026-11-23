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
/// Series Details rendered by the real app for a signed-in user with the
/// series in their library: the hero (decorative art, progress bar, action
/// row with its menu, heart and Share), the section links, the season cards
/// and their forms. What each piece decides is tested on the page model
/// (<c>SeriesDetailsGetTests</c>); this is about the markup that carries it.
/// </summary>
[TestFixture]
public sealed class SeriesDetailsPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const int SeriesId = 78;

    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static EpisodeSummary Ep(int id, int season, int number) =>
        new() { Id = id, SeasonNumber = season, EpisodeNumber = number, Name = $"Episode {id}", Aired = new DateOnly(2020, 1, 1).AddDays(id), RuntimeMinutes = 45 };

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        _baseFactory.TheTvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId,
                Name = "Hero Test Series",
                Slug = "hero-test-series",
                ImageUrl = "https://artworks.thetvdb.com/banners/posters/78-1.jpg",
                BackgroundUrl = "https://artworks.thetvdb.com/banners/fanart/78-1_t.jpg",
                Network = "Discovery",
                FirstAired = new DateOnly(2020, 1, 2),
                Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                Characters =
                [
                    new Character { Id = 1, PeopleId = 1, PersonName = "Ada Actor", Name = "The Lead", PeopleType = "Actor" }
                ],
                Seasons = Enumerable.Range(0, 6).Select(n => new SeasonSummary { Id = n + 1, Number = n }).ToList(),
                Episodes =
                [
                    Ep(1, 0, 1),
                    Ep(11, 1, 1), Ep(21, 2, 1), Ep(31, 3, 1), Ep(41, 4, 1), Ep(51, 5, 1), Ep(52, 5, 2)
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
        _client.DefaultRequestHeaders.Add(UserHeader, UserId.ToString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "hero-user", Email = "hero@test.local" });
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
             VALUES ({Guid.NewGuid()}, {UserId}, {SeriesId}, {"Hero Test Series"}, {now}, {now}, 1)
             """);
        // Seasons 1 to 4 watched: the next episode is S05E01.
        foreach (var episode in new[] { 11, 21, 31, 41 })
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, source, created_utc, updated_utc)
                 VALUES ({Guid.NewGuid()}, {UserId}, {SeriesId}, {episode}, {now}, {"Single"}, {now}, {now})
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
    public async Task TheHero_Should_DrawItsArtAsDecoration_AndThePosterEagerly()
    {
        var html = await PageAsync();

        Regex.Matches(html, "<h1[ >]").Count.Should().Be(1);
        html.Should().MatchRegex("<img class=\"tvdb-hero__art\" src=\"[^\"]+fanart[^\"]+\" alt=\"\" fetchpriority=\"high\" loading=\"eager\"");
        html.Should().MatchRegex("<img src=\"[^\"]+posters[^\"]+\" alt=\"Poster for Hero Test Series\" fetchpriority=\"high\" loading=\"eager\"");
        html.Should().Contain("2020– · 5 seasons · Discovery");
        html.Should().Contain("<a class=\"tvdb-back-link tvdb-hero__back\" href=\"/Library\">", "a series in the library leads back to the Library");
    }

    [Test]
    public async Task TheProgressBar_Should_SayItsFiguresToAssistiveTechnology()
    {
        var html = await PageAsync();

        html.Should().Contain("role=\"progressbar\" aria-label=\"Episodes watched\" aria-valuemin=\"0\" aria-valuemax=\"6\" aria-valuenow=\"4\"");
        html.Should().Contain("aria-valuetext=\"4 of 6 episodes watched, 66%\"");
        html.Should().Contain("4 of 6 episodes · 66%").And.Contain("2 left · 1 h 30 min");
    }

    [Test]
    public async Task TheActionRow_Should_HaveThePrimary_AMenu_TheHeart_AndShare_EachWithAName()
    {
        var html = await PageAsync();

        html.Should().Contain("Mark S05E01 watched");
        html.Should().Contain("aria-label=\"More actions for Hero Test Series\"");
        html.Should().MatchRegex("id=\"stopWatching\">\\s*<i[^>]*></i> Stop watching");
        html.Should().Contain("data-bs-target=\"#removeFromLibraryModal\"");
        html.Should().Contain("id=\"removeFromLibraryModal\"", "Remove from library is confirmed first");
        html.Should().Contain("aria-label=\"Like this series\"");
        html.Should().Contain("aria-label=\"Share Hero Test Series\"");
        html.Should().NotContain("tvdb-quiet-action", "the loose links under the details are gone");

        // The "Link copied." toast, drawn by _Toast for the script to show.
        var template = Regex.Match(html, "<template data-toast-template=\"link-copied\">(.*?)</template>", RegexOptions.Singleline);
        template.Success.Should().BeTrue();
        template.Groups[1].Value.Should().Contain("alert-info").And.Contain("data-toast-duration=\"5000\"").And.Contain("Link copied.");
    }

    [Test]
    public async Task TheSectionLinks_Should_IncludeJumpToNext_PointingAtTheNextEpisodesRow()
    {
        var html = await PageAsync();

        html.Should().Contain("<a href=\"#episodes\">Episodes</a>").And.Contain("<a href=\"#overview\">Overview</a>").And.Contain("<a href=\"#cast\">Cast</a>");
        html.Should().Contain("href=\"#episode-51\" class=\"tvdb-section-links__next\"");
        html.Should().Contain("id=\"episode-51\"");
    }

    [Test]
    public async Task TheSeasons_Should_BeCards_WithTheEarlierOnesGrouped_AndTheSpecialsLast()
    {
        var html = await PageAsync();

        html.Should().Contain("Show 2 earlier seasons");
        html.Should().MatchRegex("id=\"season-5\">\\s*<details class=\"tvdb-season-card__details\" open=\"open\">", "the season of the next episode is open");
        html.Should().MatchRegex("id=\"season-4\">\\s*<details class=\"tvdb-season-card__details\">");
        html.Should().Contain("not counted in progress");
        html.IndexOf("id=\"season-0\"", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("id=\"season-5\"", StringComparison.Ordinal));
        html.Should().Contain("All watched", "seasons 1 to 4 are");
        html.Should().Contain("aria-label=\"Actions for Season 5\"");
    }

    [Test]
    public async Task AnEpisodeRow_Should_PostBackToItself_AndTheRedirectShouldEndThere()
    {
        var html = await PageAsync();

        var row = Regex.Match(html, "<li class=\"tvdb-ep [^\"]*\" id=\"episode-52\">.*?</li>", RegexOptions.Singleline).Value;
        row.Should().Contain("data-prior-check=\"true\"").And.Contain("aria-pressed=\"false\"").And.Contain("aria-label=\"E2 Episode 52 watched\"");
        row.Should().Contain("name=\"Anchor\" value=\"episode-52\"");

        var response = await _client.PostAsync(
            $"/Series/Details/{SeriesId}?handler=ToggleEpisodeWatched",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["episodeId"] = "11",
                ["Season"] = "1",
                ["Anchor"] = "episode-11",
                ["__RequestVerificationToken"] = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().EndWith("season=1#episode-11");

        // Put the watch back, so the other tests see four seasons watched.
        var again = await PageAsync();
        await _client.PostAsync(
            $"/Series/Details/{SeriesId}?handler=ToggleEpisodeWatched",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["episodeId"] = "11",
                ["__RequestVerificationToken"] = Regex.Match(again, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value
            }));
    }

    /// <summary>The page with its whitespace collapsed and its entities decoded ("–", "·"), for plain matching.</summary>
    private async Task<string> PageAsync() =>
        WebUtility.HtmlDecode(Regex.Replace(await _client.GetStringAsync($"/Series/Details/{SeriesId}"), @"\s+", " "));

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers[UserHeader], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = RecallPrincipal.Create(userId, "hero-user", "hero@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
