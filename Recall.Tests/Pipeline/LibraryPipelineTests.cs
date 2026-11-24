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
/// The Library rendered by the real app: a Watching card carries the round
/// check that marks the series' next episode, with its name and the form that
/// brings the page back to the card; a card with no released next episode, and
/// a card in another section, has none. What the page model decides is tested
/// in <c>LibraryModelTests</c>.
/// </summary>
[TestFixture]
public sealed class LibraryPipelineTests
{
    private const string UserHeader = "X-Test-User";

    private static readonly Guid UserId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private const int Behind = 301;        // S01E01 watched, S01E02 released: the check marks S01E02
    private const int NotStarted = 302;    // one released episode, nothing watched: the check marks S01E01
    private const int UpToDate = 303;      // everything released is watched: Up to date, no check
    private const int NothingAired = 304;  // in Watching, but nothing to mark

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        var released = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        SetUpSeries(Behind, "Library Behind", Ep(Behind, 1, released), Ep(Behind, 2, released));
        SetUpSeries(NotStarted, "Library Not Started", Ep(NotStarted, 1, released));
        SetUpSeries(UpToDate, "Library Up To Date", Ep(UpToDate, 1, released), Ep(UpToDate, 2, future));
        SetUpSeries(NothingAired, "Library Nothing Aired", Ep(NothingAired, 1, future));

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
        _client.DefaultRequestHeaders.Add(UserHeader, UserId.ToString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "library-user", Email = "library@test.local" });

        var now = DateTime.UtcNow;
        foreach (var (seriesId, episodeId) in new[] { (Behind, Behind * 100 + 1), (UpToDate, UpToDate * 100 + 1) })
        {
            db.EpisodeWatches.Add(new EpisodeWatchEntity
            {
                Id = Guid.NewGuid(), UserId = UserId, SeriesTvdbId = seriesId, EpisodeTvdbId = episodeId,
                WatchedUtc = now.AddHours(-1), Source = WatchSource.Single
            });
        }

        await db.SaveChangesAsync();

        foreach (var id in new[] { Behind, NotStarted, UpToDate, NothingAired })
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
                 VALUES ({Guid.NewGuid()}, {UserId}, {id}, {$"Series {id}"}, {now.AddMinutes(-id)}, {now}, 1)
                 """);
        }
    }

    private static EpisodeSummary Ep(int seriesId, int number, DateOnly aired) =>
        new() { Id = seriesId * 100 + number, SeasonNumber = 1, EpisodeNumber = number, Name = $"Episode {number}", Aired = aired };

    private void SetUpSeries(int id, string name, params EpisodeSummary[] episodes) =>
        _baseFactory.TheTvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = id,
                Name = name,
                Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                Seasons = [new SeasonSummary { Id = id, Number = 1 }],
                Episodes = episodes
            });

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Test]
    public async Task AWatchingCard_Should_HaveTheCheck_ForItsNextEpisode()
    {
        var html = await PageAsync("/Library");

        var card = Card(html, Behind);
        card.Should().Contain($"<form method=\"post\" action=\"/Library?seriesId={Behind}&episodeId={Behind * 100 + 2}&handler=MarkWatched\">");
        card.Should().Contain($"<input type=\"hidden\" name=\"Anchor\" value=\"series-card-{Behind}\" />",
            "the page comes back to the card");
        card.Should().Contain("<button type=\"submit\" class=\"tvdb-round-button tvdb-round-button--lg tvdb-round-button--on-image\" title=\"Mark as watched\" aria-label=\"Mark Library Behind S01E02 watched\">");

        Card(html, NotStarted).Should().Contain("aria-label=\"Mark Library Not Started S01E01 watched\"",
            "a series never started marks its first episode");
    }

    [TestCase(UpToDate)]
    [TestCase(NothingAired)]
    public async Task ACardWithNoReleasedNextEpisode_Should_HaveNoCheck(int seriesId)
    {
        var html = await PageAsync("/Library");

        var card = Card(html, seriesId);
        card.Should().NotBeEmpty();
        card.Should().NotContain("tvdb-series-card-mark").And.NotContain("handler=MarkWatched");
    }

    [Test]
    public async Task TheOneSectionView_Should_PostItsSectionWithTheCheck()
    {
        var html = await PageAsync("/Library?section=watching");

        var card = Card(html, Behind);
        card.Should().Contain("aria-label=\"Mark Library Behind S01E02 watched\"");
        Regex.Match(card, "<form method=\"post\" action=\"[^\"]*handler=MarkWatched\">(.*?)</form>").Groups[1].Value
            .Should().Contain("<input type=\"hidden\" name=\"Section\" value=\"watching\" />", "the mark comes back to this view");
    }

    [Test]
    public async Task ThePage_Should_HaveNoDuplicateIds()
    {
        var html = await PageAsync("/Library");

        Regex.Matches(html, " id=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty();
    }

    /// <summary>One series' card, from its opening tag to the next card (or the end of its grid).</summary>
    private static string Card(string html, int seriesId) =>
        Regex.Match(html, $"<div class=\"tvdb-series-card-wrap\" data-content-type=\"series\" id=\"series-card-{seriesId}\">(.*?)(?=<div class=\"tvdb-series-card-wrap\"|</section>)").Groups[1].Value;

    /// <summary>The page with its whitespace collapsed and its entities decoded, for plain matching.</summary>
    private async Task<string> PageAsync(string path)
    {
        var response = await _client.GetAsync(path);
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

            var principal = RecallPrincipal.Create(userId, "library-user", "library@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
