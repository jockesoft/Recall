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
/// Episode Details rendered by the real app: the shared hero without a poster
/// (the series above the title, the meta line, the place in the season), the
/// action row, the Up next card and its check, the guest stars and the
/// previous/next cards. What each piece decides is tested on the page model
/// (<c>EpisodeDetailsPageTests</c>); this is the markup that carries it, and
/// the Up next check posting through the real pipeline.
/// </summary>
[TestFixture]
public sealed class EpisodeDetailsPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const int SeriesId = 79;
    private const int E1 = 101, E2 = 102, E3 = 103;

    private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpClient _visitor = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        var episodes = new[]
        {
            new EpisodeSummary { Id = E1, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot", Aired = new DateOnly(2020, 1, 5), RuntimeMinutes = 68, Image = "https://artworks.thetvdb.com/banners/episodes/101.jpg" },
            new EpisodeSummary { Id = E2, SeasonNumber = 1, EpisodeNumber = 2, Name = "The Second One", Aired = new DateOnly(2020, 1, 12), RuntimeMinutes = 68, Image = "https://artworks.thetvdb.com/banners/episodes/102.jpg" },
            new EpisodeSummary { Id = E3, SeasonNumber = 1, EpisodeNumber = 3, Name = "Far Ahead", Aired = new DateOnly(2099, 1, 1) },
        };
        _baseFactory.TheTvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId, Name = "Episode Test Series", Slug = "episode-test-series", AirsTime = "21:00", OriginalCountry = "usa",
                BackgroundUrl = "https://artworks.thetvdb.com/banners/fanart/79-1_t.jpg",
                Episodes = episodes
            });
        foreach (var e in episodes)
        {
            _baseFactory.TheTvDb
                .Setup(x => x.GetEpisodeDetailsAsync(e.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Episode
                {
                    Id = e.Id, SeriesId = SeriesId, SeasonNumber = e.SeasonNumber, Number = e.EpisodeNumber, Name = e.Name,
                    Aired = e.Aired?.ToString("yyyy-MM-dd"), Runtime = e.RuntimeMinutes, Image = e.Image,
                    Characters = e.Id == E1
                        ? [new Character { Id = 1, PeopleId = 7, PersonName = "Gwen Guest", PeopleType = "Guest Star", Name = "The Visitor", Url = "https://thetvdb.com/people/7" }]
                        : []
                });
        }

        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName);
            }));

        var options = new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        };
        _client = _factory.CreateClient(options);
        _client.DefaultRequestHeaders.Add(UserHeader, UserId.ToString());
        _visitor = _factory.CreateClient(options);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "episode-user", Email = "episode@test.local" });
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
             VALUES ({Guid.NewGuid()}, {UserId}, {SeriesId}, {"Episode Test Series"}, {now}, {now}, 1)
             """);
        // The pilot is watched; the second episode is up next.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, source, created_utc, updated_utc)
             VALUES ({Guid.NewGuid()}, {UserId}, {SeriesId}, {E1}, {now}, {"Single"}, {now}, {now})
             """);
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _visitor.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Test]
    public async Task TheHero_Should_HaveTheStillBehindIt_TheSeriesAboveTheTitle_AndNoPoster()
    {
        var html = await PageAsync(_client, E1);

        Regex.Matches(html, "<h1[ >]").Count.Should().Be(1);
        html.Should().Contain("class=\"tvdb-hero has-art tvdb-hero--no-poster\"");
        html.Should().MatchRegex("<img class=\"tvdb-hero__art\" src=\"[^\"]+episodes/101.jpg\" alt=\"\" fetchpriority=\"high\" loading=\"eager\"");
        html.Should().NotContain("Poster for", "an episode has no poster");
        html.Should().Contain("<a class=\"tvdb-back-link tvdb-hero__back\" href=\"/Series/Details/79\">");
        html.Should().Contain("<a class=\"tvdb-hero__parent tvdb-link-plain\" href=\"/Series/Details/79\">Episode Test Series</a>");
        html.Should().Contain("<h1 class=\"tvdb-hero__title\">Pilot</h1>");
        html.Should().Contain("<p class=\"tvdb-hero__meta\"><span class=\"tvdb-mono\">S01</span> · <span class=\"tvdb-mono\">E1</span> · 1 h 8 min · Aired Jan 5, 2020</p>",
            "the code is data in mono, the dot between its parts is not");
    }

    [Test]
    public async Task WherePeopleAre_Should_BeThePlaceInTheSeason_WithABarThatSaysItsFigures()
    {
        var html = await PageAsync(_client, E1);

        html.Should().Contain("Episode 1 of 3 · Season 1").And.Contain("1 of 2 watched");
        html.Should().Contain("role=\"progressbar\" aria-label=\"Episodes of season 1 watched\" aria-valuemin=\"0\" aria-valuemax=\"2\" aria-valuenow=\"1\"");
    }

    [Test]
    public async Task AWatchedEpisode_Should_BeTheStatusBox_WithMarkAsUnwatchedInTheMenu()
    {
        var html = await PageAsync(_client, E1);

        html.Should().MatchRegex("<span class=\"tvdb-status-box__label\">Watched [A-Z][a-z]{2}, [A-Z][a-z]{2} \\d+</span>");
        html.Should().MatchRegex("id=\"markUnwatched\">\\s*<i[^>]*></i> Mark as unwatched");
        html.Should().Contain("aria-label=\"More actions for Pilot\"");
        html.Should().Contain("aria-label=\"Like this episode\"");
        html.Should().Contain("aria-label=\"Share Episode Test Series: Pilot\"");
    }

    [Test]
    public async Task UpNext_Should_LinkTheNextEpisode_WithItsLazyStill_AndTheCheck()
    {
        var html = await PageAsync(_client, E1);

        var card = Regex.Match(html, "<section class=\"tvdb-up-next tvdb-surface-paper\".*?</section>", RegexOptions.Singleline).Value;
        card.Should().Contain("<h2 class=\"tvdb-eyebrow tvdb-up-next__eyebrow\" id=\"upNextHeading\">Up next</h2>");
        card.Should().MatchRegex("<img src=\"[^\"]+episodes/102.jpg\" alt=\"\" loading=\"lazy\"");
        card.Should().Contain("href=\"/Episodes/Details/102\"><span class=\"tvdb-mono\">S01E02</span> · The Second One</a>");
        card.Should().Contain("Aired Jan 12, 2020");
        card.Should().Contain("data-prior-check=\"true\" data-episode-id=\"102\" data-prior-count=\"0\"");
        card.Should().Contain("<input type=\"hidden\" name=\"episodeId\" value=\"102\" />");
        card.Should().Contain("aria-pressed=\"false\"").And.Contain("aria-label=\"S01E02 The Second One watched\"");
    }

    [Test]
    public async Task UpNext_Should_BeTheDashedClock_ForAnEpisodeNotAiredYet()
    {
        var html = await PageAsync(_client, E2);

        var card = Regex.Match(html, "<section class=\"tvdb-up-next tvdb-surface-paper\".*?</section>", RegexOptions.Singleline).Value;
        card.Should().Contain("S01E03</span> · Far Ahead");
        card.Should().Contain("tvdb-ep-mark--upcoming tvdb-up-next__control\" role=\"img\" aria-label=\"Not aired yet\"");
        card.Should().NotContain("<form", "an episode that may not be marked has no button");
        card.Should().Contain("Airs <time datetime=\"2099-01-02T02:00:00Z\" data-local-release=\"datetime\">");
    }

    [Test]
    public async Task TheNextEpisode_Should_OfferMarkAsWatched_WithoutAQuestion_WhenNothingEarlierIsUnwatched()
    {
        var html = await PageAsync(_client, E2);

        html.Should().Contain("data-prior-check=\"false\"");
        html.Should().MatchRegex("<button type=\"submit\" class=\"btn btn-primary tvdb-primary-action\" id=\"primaryAction\">\\s*<span class=\"tvdb-primary-action__label\">\\s*<i class=\"ph ph-check\" aria-hidden=\"true\"></i>\\s*Mark as watched");
        html.Should().Contain("id=\"seriesCatchUpModal\"", "the \"Catch up?\" dialog is on the page for the Up next check");
    }

    [Test]
    public async Task GuestStars_Should_BeListed_WhenTheRecordHasThem_AndLeftOutOtherwise()
    {
        var withGuests = await PageAsync(_client, E1);
        withGuests.Should().Contain("Guest stars <span class=\"tvdb-count\">1</span>");
        withGuests.Should().Contain(">Gwen Guest</a>").And.Contain("<span class=\"tvdb-person__detail\">The Visitor</span>");

        var without = await PageAsync(_client, E2);
        without.Should().NotContain("Guest stars");
    }

    [Test]
    public async Task PreviousAndNext_Should_BeTwoCards_WithAGapWhereOneIsMissing()
    {
        var first = await PageAsync(_client, E1);
        var nav = Regex.Match(first, "<nav class=\"tvdb-episode-nav\".*?</nav>", RegexOptions.Singleline).Value;
        nav.Should().StartWith("<nav class=\"tvdb-episode-nav\" aria-label=\"Previous and next episode\"> <span class=\"tvdb-episode-nav__gap\" aria-hidden=\"true\"></span>");
        nav.Should().Contain("rel=\"next\" href=\"/Episodes/Details/102\"").And.Contain("<span class=\"tvdb-episode-nav__title\"><span class=\"tvdb-mono\">S01E02</span> · The Second One</span>");

        var middle = await PageAsync(_client, E2);
        Regex.Match(middle, "<nav class=\"tvdb-episode-nav\".*?</nav>", RegexOptions.Singleline).Value
            .Should().Contain("rel=\"prev\"").And.Contain("rel=\"next\"").And.NotContain("tvdb-episode-nav__gap");
    }

    [Test]
    public async Task ThePage_Should_HaveNoDuplicateIds()
    {
        var html = await PageAsync(_client, E1);

        var ids = Regex.Matches(html, " id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task TheUpNextCheck_Should_MarkTheNextEpisode_AndComeBackToThisOne()
    {
        var html = await PageAsync(_client, E1);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var response = await _client.PostAsync(
            $"/Episodes/Details/{E1}?handler=ToggleWatched",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["episodeId"] = E2.ToString(), ["__RequestVerificationToken"] = token }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be($"/Episodes/Details/{E1}");

        var after = await PageAsync(_client, E1);
        Regex.Match(after, "<section class=\"tvdb-up-next tvdb-surface-paper\".*?</section>", RegexOptions.Singleline).Value
            .Should().Contain("tvdb-ep-mark tvdb-ep-mark--on").And.Contain("aria-pressed=\"true\"")
            .And.Contain("id=\"upNextHeading\">Next episode</h2>", "the next episode is watched now");
        after.Should().Contain("2 of 2 watched");

        // And back, so the other tests see the second episode unwatched.
        await _client.PostAsync(
            $"/Episodes/Details/{E1}?handler=ToggleWatched",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["episodeId"] = E2.ToString(), ["__RequestVerificationToken"] = token }));
        (await PageAsync(_client, E1)).Should().Contain("1 of 2 watched");
    }

    [Test]
    public async Task AVisitor_Should_GetTheActionRow_AndUpNextWithoutAControl()
    {
        var html = await PageAsync(_visitor, E1);

        html.Should().NotContain("Sign in to track this episode.", "the sign-in card is gone");
        html.Should().Contain($"action=\"/Episodes/Details/{E1}?handler=ToggleWatched\"").And.Contain("id=\"primaryAction\"");
        html.Should().Contain($"action=\"/Episodes/Details/{E1}?handler=ToggleLike\"");
        html.Should().Contain($"action=\"/Episodes/Details/{E1}?handler=RateEpisode\"").And.Contain("tvdb-rating-disclosure");
        html.Should().Contain("data-prior-check=\"false\"", "a visitor is not asked about earlier episodes");
        Regex.Match(html, "<section class=\"tvdb-up-next tvdb-surface-paper\".*?</section>", RegexOptions.Singleline).Value
            .Should().Contain("S01E02").And.NotContain("tvdb-ep-mark");
        html.Should().NotContain("seriesCatchUpModal");
        html.Should().Contain("Episode 1 of 3 · Season 1").And.NotContain("role=\"progressbar\"");
    }

    /// <summary>The page with its whitespace collapsed and its entities decoded ("·"), for plain matching.</summary>
    private static async Task<string> PageAsync(HttpClient client, int episodeId) =>
        WebUtility.HtmlDecode(Regex.Replace(await client.GetStringAsync($"/Episodes/Details/{episodeId}"), @"\s+", " "));

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers[UserHeader], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = RecallPrincipal.Create(userId, "episode-user", "episode@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
