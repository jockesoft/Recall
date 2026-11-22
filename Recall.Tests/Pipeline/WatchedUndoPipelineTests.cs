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
/// "Mark season watched" through the whole app: the handler writes the toast
/// and its Undo to TempData, the real cookie carries them over the redirect,
/// and the next page draws an Undo button that undoes the batch. Until
/// 2026-11-22 the button never appeared: the cookie returns the batch id as a
/// <see cref="Guid"/> and the toast model read it "as string".
/// </summary>
[TestFixture]
public sealed class WatchedUndoPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const int SeriesId = 77;

    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        _baseFactory = new RecallWebApplicationFactory();
        _baseFactory.TheTvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId,
                Name = "Undo Test Series",
                Slug = "undo-test-series",
                FirstAired = new DateOnly(2020, 1, 1),
                Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                Seasons = [new SeasonSummary { Id = 1, Number = 1, Name = "Season 1" }],
                Episodes =
                [
                    new EpisodeSummary { Id = 7701, SeasonNumber = 1, EpisodeNumber = 1, Name = "One", Aired = new DateOnly(2020, 1, 1) },
                    new EpisodeSummary { Id = 7702, SeasonNumber = 1, EpisodeNumber = 2, Name = "Two", Aired = new DateOnly(2020, 1, 8) },
                    new EpisodeSummary { Id = 7703, SeasonNumber = 1, EpisodeNumber = 3, Name = "Three", Aired = new DateOnly(2020, 1, 15) }
                ]
            });

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
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "undo-user", Email = "undo@test.local" });
        await db.SaveChangesAsync();

        // Already in the library, so the mark does not try to add it (tracked_series
        // cannot be inserted through EF on SQLite: its xmin token).
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tracked_series (id, user_id, tvdb_id, name, created_utc, updated_utc, xmin)
             VALUES ({Guid.NewGuid()}, {UserId}, {SeriesId}, {"Undo Test Series"}, {now}, {now}, 1)
             """);
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [Test]
    public async Task MarkSeasonWatched_Should_RenderAnUndoButton_ThatUndoesTheBatch()
    {
        var page = await _client.GetStringAsync($"/Series/Details/{SeriesId}");
        page.Should().Contain("handler=MarkSeasonWatched");

        var mark = await _client.PostAsync(
            $"/Series/Details/{SeriesId}?handler=MarkSeasonWatched",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Season"] = "1",
                ["__RequestVerificationToken"] = Token(page)
            }));

        mark.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var afterMark = await _client.GetStringAsync(mark.Headers.Location!);

        afterMark.Should().Contain("Marked 3 episodes as watched.");
        var undoForm = Regex.Match(afterMark, "<form[^>]*handler=UndoWatched[^>]*>.*?</form>", RegexOptions.Singleline);
        undoForm.Success.Should().BeTrue("a season mark's toast offers Undo");
        undoForm.Value.Should().Contain(">Undo<");

        var batch = Regex.Match(undoForm.Value, "name=\"batch\" value=\"([^\"]+)\"").Groups[1].Value;
        (await BatchIdsAsync()).Should().ContainSingle().Which.Should().Be(Guid.Parse(batch),
            "the button carries the id of the batch the mark wrote");

        var undo = await _client.PostAsync(
            Regex.Match(undoForm.Value, "action=\"([^\"]+)\"").Groups[1].Value.Replace("&amp;", "&"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["batch"] = batch,
                ["__RequestVerificationToken"] = Token(undoForm.Value)
            }));

        undo.StatusCode.Should().Be(HttpStatusCode.Redirect);
        WebUtility.HtmlDecode(await _client.GetStringAsync(undo.Headers.Location!))
            .Should().Contain("Undone — 3 episodes marked as not watched.");
        (await BatchIdsAsync()).Should().BeEmpty("the Undo removed the season's watches");
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<List<Guid?>> BatchIdsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EpisodeWatches
            .Where(w => w.UserId == UserId && w.SeriesTvdbId == SeriesId)
            .Select(w => w.BatchId)
            .Distinct()
            .ToListAsync();
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

            var principal = RecallPrincipal.Create(userId, "undo-user", "undo@test.local", UserRole.User);
            var identity = new ClaimsIdentity(principal.Claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
