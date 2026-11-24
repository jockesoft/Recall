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
/// A one-tap mark on the Dashboard through the real app, followed as a
/// browser follows it: the redirect returns to the same place in Continue
/// watching (not to the series' new place at the top), and the card and row of
/// the series just marked follow the image rule every page shares
/// (<c>EpisodeArt.Resolve</c>): its next episode has no still, in the
/// aggregate or on its own record, so it shows the series' background art.
/// </summary>
[TestFixture]
public sealed class ContinueWatchingMarkPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const int ArtSeries = 401;
    private const string Background = "https://artworks.thetvdb.com/banners/fanart/original/401.jpg";
    private const string FirstStill = "https://artworks.thetvdb.com/banners/episodes/401/1.jpg";

    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444401");

    /// <summary>A second user, with E1 of the Art Show and of 402 already watched: the Art Show at place 2, with a card and a row.</summary>
    private static readonly Guid WatchedUserId = Guid.Parse("44444444-4444-4444-4444-444444444402");
    private static readonly int[] OtherSeries = [402, 403];

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();

        // E1 has a still; E2 has none, in the aggregate or on its own record (the
        // mock's default is no record), but the series has background art.
        _baseFactory.TheTvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(ArtSeries, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = ArtSeries,
                Name = "Art Show",
                BackgroundUrl = Background,
                Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                Seasons = [new SeasonSummary { Id = 1, Number = 1 }],
                Episodes =
                [
                    new EpisodeSummary { Id = 40101, SeasonNumber = 1, EpisodeNumber = 1, Name = "With a still", Aired = new DateOnly(2020, 1, 1), Image = FirstStill },
                    new EpisodeSummary { Id = 40102, SeasonNumber = 1, EpisodeNumber = 2, Name = "Without a still", Aired = new DateOnly(2020, 1, 8) },
                    new EpisodeSummary { Id = 40103, SeasonNumber = 1, EpisodeNumber = 3, Name = "Later", Aired = new DateOnly(2020, 1, 15), Image = FirstStill }
                ]
            });

        foreach (var id in OtherSeries)
        {
            _baseFactory.TheTvDb
                .Setup(x => x.GetSeriesAggregateByIdAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesAggregate
                {
                    TvdbId = id,
                    Name = $"Other Show {id}",
                    Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                    Seasons = [new SeasonSummary { Id = id, Number = 1 }],
                    Episodes =
                    [
                        new EpisodeSummary { Id = id * 100 + 1, SeasonNumber = 1, EpisodeNumber = 1, Name = $"Pilot {id}", Aired = new DateOnly(2020, 1, 1), Image = $"https://artworks.thetvdb.com/banners/episodes/{id}.jpg" },
                        new EpisodeSummary { Id = id * 100 + 2, SeasonNumber = 1, EpisodeNumber = 2, Name = $"Second {id}", Aired = new DateOnly(2020, 1, 8), Image = $"https://artworks.thetvdb.com/banners/episodes/{id}.jpg" }
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

        // Cookies kept, as a browser keeps them: the antiforgery cookie and the TempData cookie.
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        _client.DefaultRequestHeaders.Add(UserHeader, UserId.ToString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "art-user", Email = "art@test.local" });
        db.AppUsers.Add(new AppUserEntity { Id = WatchedUserId, Username = "art-watched", Email = "art-watched@test.local" });

        // The second user has watched 402's E1 an hour ago and the Art Show's E1
        // two hours ago: 402, the Art Show, 403.
        var now = DateTime.UtcNow;
        db.EpisodeWatches.Add(new EpisodeWatchEntity { UserId = WatchedUserId, SeriesTvdbId = 402, EpisodeTvdbId = 40201, WatchedUtc = now.AddHours(-1) });
        db.EpisodeWatches.Add(new EpisodeWatchEntity { UserId = WatchedUserId, SeriesTvdbId = ArtSeries, EpisodeTvdbId = 40101, WatchedUtc = now.AddHours(-2) });
        await db.SaveChangesAsync();

        // The first user has watched nothing, so the list is in the order the
        // series were added, newest first: 402, 403, then the Art Show last, at place 3.
        foreach (var user in new[] { UserId, WatchedUserId })
        {
            foreach (var (id, minutesAgo) in new[] { (402, 1), (403, 2), (ArtSeries, 3) })
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
                     VALUES ({Guid.NewGuid()}, {user}, {id}, {$"Show {id}"}, {now.AddMinutes(-minutesAgo)}, {now}, 1)
                     """);
            }
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
    public async Task AMark_Should_ReturnToTheSamePlace_AndTheMarkedSeries_ShowsItsBackgroundArt()
    {
        var before = await _client.GetStringAsync("/Dashboard");
        CardOf(before, ArtSeries).Should().Contain($"src=\"{FirstStill}\"", "E1 has a still of its own");
        CardOf(before, ArtSeries).Should().Contain("id=\"catchup-card-3\"", "the Art Show is third, and last");

        var form = Regex.Match(before,
            $"<form method=\"post\" class=\"tvdb-catchup-check\" action=\"([^\"]*seriesId={ArtSeries}[^\"]*)\">(.*?)</form>",
            RegexOptions.Singleline);
        form.Success.Should().BeTrue("the card has its one-tap check");
        form.Groups[2].Value.Should().Contain("name=\"Anchor\" value=\"catchup-card-3\"")
            .And.Contain("name=\"last\" value=\"true\"", "it is the last of the list");

        var mark = await _client.PostAsync(
            WebUtility.HtmlDecode(form.Groups[1].Value),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["all"] = "false",
                ["Anchor"] = "catchup-card-3",
                ["last"] = "true",
                ["__RequestVerificationToken"] = Token(form.Groups[2].Value)
            }));

        mark.StatusCode.Should().Be(HttpStatusCode.Redirect);
        mark.Headers.Location!.OriginalString.Should().Be("/Dashboard#catchup-card-3",
            "the page returns to the same place, not to the top where the series has moved");

        var after = await _client.GetStringAsync(mark.Headers.Location);
        after.Should().Contain("Marked Art Show S01E01 as watched.");

        // Just watched, so first now; its next episode, E2, has no still anywhere.
        var card = CardOf(after, ArtSeries);
        card.Should().Contain("id=\"catchup-card-1\"");
        card.Should().Contain($"<img src=\"{Background}\"", "an episode without a still shows the series' background art");
        card.Should().NotContain("is-empty");
        after.Should().Contain("id=\"catchup-card-3\"", "the place returned to is still on the page, with another series");
        _baseFactory.TheTvDb.Verify(x => x.GetEpisodeDetailsAsync(40102, It.IsAny<CancellationToken>()), Times.AtLeastOnce(),
            "the episode's own record is looked at for a still before the background art is used");
    }

    [Test]
    public async Task ASeriesWhoseNextEpisodeHasNoStill_Should_ShowItsBackgroundArt_OnItsCardAndRow()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Dashboard");
        request.Headers.Remove(UserHeader);
        request.Headers.Add(UserHeader, WatchedUserId.ToString());
        var html = await (await _client.SendAsync(request)).Content.ReadAsStringAsync();

        var card = CardOf(html, ArtSeries);
        card.Should().Contain("id=\"catchup-card-2\"");
        card.Should().Contain($"<img src=\"{Background}\"", "E2 has no still, in the aggregate or on its own record");
        card.Should().NotContain("is-empty");

        var row = Regex.Match(html, "<li class=\"tvdb-catchup-row\" id=\"catchup-row-2\">(.*?)</li>", RegexOptions.Singleline).Groups[1].Value;
        row.Should().Contain($"/Series/Details/{ArtSeries}\"");
        row.Should().Contain($"<img src=\"{Background}\"", "the phone's row follows the same rule");
        row.Should().NotContain("is-empty");

        // 403's next episode has a still of its own: it keeps it.
        CardOf(html, 403).Should().Contain("src=\"https://artworks.thetvdb.com/banners/episodes/403.jpg\"");
    }

    /// <summary>A series' card, from its opening tag to the next card or the rows.</summary>
    private static string CardOf(string html, int seriesId) =>
        Regex.Matches(html, "<div class=\"tvdb-catchup-card\"(.*?)(?=<div class=\"tvdb-catchup-card\"|<ul class=\"tvdb-catchup-rows|</section>)", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Single(card => card.Contains($"/Series/Details/{seriesId}\"", StringComparison.Ordinal));

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers[UserHeader], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = RecallPrincipal.Create(userId, "art-user", "art@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
