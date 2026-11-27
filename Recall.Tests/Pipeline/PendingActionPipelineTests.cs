using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Services.Metrics;
using Recall.Web.Services.PendingActions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pipeline;

/// <summary>
/// A signed-out visitor presses an action on a Details page and gets it done
/// by signing in, through the whole application: the press is kept in the
/// signed cookie (nothing in the address), the sign-in page says what will
/// happen, the email's link and its code both return to the page, the action
/// is done once with its usual toast, and each step is counted. Its own
/// application instance per fixture, so its sign-in requests have a rate-limit
/// bucket of their own (8 requests for an email per 5 minutes per IP).
/// </summary>
public abstract class PendingActionPipelineFixture
{
    protected const int SeriesId = RecallWebApplicationFactory.KnownSeriesId;
    protected const int MovieId = 79;
    protected const string MovieName = "Pending Test Movie";

    private RecallWebApplicationFactory _baseFactory = null!;
    protected WebApplicationFactory<Program> _factory = null!;
    protected readonly Mock<ISeriesLibraryService> _library = new();

    [OneTimeSetUp]
    public void StartApplication()
    {
        _baseFactory = new RecallWebApplicationFactory();
        var tvDb = _baseFactory.TheTvDb;

        var series = tvDb.Object.GetSeriesAggregateByIdAsync(SeriesId).GetAwaiter().GetResult();
        tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(series);

        var movie = new MovieAggregate
        {
            TvdbId = MovieId,
            Name = MovieName,
            Slug = "pending-test-movie",
            ImageUrl = "https://artworks.thetvdb.com/banners/movies/79/posters/1.jpg",
            ReleaseDate = new DateOnly(2020, 5, 1),
            Status = new MovieStatus { Name = "Released" }
        };
        tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        tvDb.Setup(x => x.GetCachedMovieAggregateAsync(MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(movie);

        // A library row cannot be written through EF on SQLite (its xmin token): the add itself is the service's,
        // tested elsewhere; here it is enough to see it called for the right user and to see its toast.
        _library.Setup(x => x.AddAsync(It.IsAny<Guid>(), SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.Added, RecallWebApplicationFactory.KnownSeriesName));

        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped(_ => _library.Object)));
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    [SetUp]
    public void ClearCounts()
    {
        _factory.Services.GetRequiredService<SiteMetricsBuffer>().Drain();
        _library.Invocations.Clear();
    }

    protected HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    protected static string Antiforgery(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    protected static string Decoded(string html) => WebUtility.HtmlDecode(Regex.Replace(html, @"\s+", " "));

    /// <summary>Opens a Details page and presses one of its forms, as a visitor's browser does.</summary>
    protected static async Task<HttpResponseMessage> PressAsync(HttpClient browser, string page, string handler, Dictionary<string, string>? fields = null)
    {
        var html = await browser.GetStringAsync(page);
        var form = new Dictionary<string, string>(fields ?? []) { ["__RequestVerificationToken"] = Antiforgery(html) };
        return await browser.PostAsync($"{page}?handler={handler}", new FormUrlEncodedContent(form));
    }

    /// <summary>Asks for the sign-in email from the sign-in page the press led to; returns the page and the email's link and code.</summary>
    protected async Task<(string Page, string Link, string Code)> RequestEmailAsync(HttpClient browser, string loginLocation, string email)
    {
        var page = await browser.GetStringAsync(loginLocation);
        var returnUrl = Regex.Match(page, "<input type=\"hidden\" id=\"ReturnUrl\" name=\"ReturnUrl\" value=\"([^\"]*)\"").Groups[1].Value;

        var sent = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["ReturnUrl"] = WebUtility.HtmlDecode(returnUrl),
            // Rendered "a while ago", past the form's timing trap.
            ["FormRenderedAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeMilliseconds().ToString(),
            ["__RequestVerificationToken"] = Antiforgery(page)
        }));
        (await sent.Content.ReadAsStringAsync()).Should().Contain("Check your inbox");

        using var scope = _factory.Services.CreateScope();
        var body = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Emails
            .Where(e => e.ToAddress == email).OrderByDescending(e => e.CreatedUtc).Select(e => e.Body).FirstAsync();

        var link = Regex.Match(body, @"https://\S+/Account/Verify\?token=\S+").Value;
        var code = Regex.Match(body, @"\b\d{3} \d{3}\b").Value;
        return (page, link, code);
    }

    protected Dictionary<string, long> Counted() =>
        _factory.Services.GetRequiredService<SiteMetricsBuffer>().Pending()
            .GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));

    protected async Task<Guid> UserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AppUsers.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    protected static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
}

/// <summary>A press done after the link or the code, and what it may and may not do.</summary>
[TestFixture]
[NonParallelizable]
public sealed class PendingActionPipelineTests : PendingActionPipelineFixture
{
    [Test]
    public async Task AddToLibrary_Should_BeKept_Explained_AndDoneAfterTheLink_WithItsToast()
    {
        using var browser = Browser();

        var press = await PressAsync(browser, $"/Series/Details/{SeriesId}", "ToggleLibrary");

        press.StatusCode.Should().Be(HttpStatusCode.Redirect);
        press.Headers.Location!.OriginalString.Should().Be($"/Account/Login?returnUrl=%2FSeries%2FDetails%2F{SeriesId}",
            "the page to return to, and nothing about the action");
        SetCookies(press).Should().ContainSingle(c => c.StartsWith(PendingActions.CookieName + "="))
            .Which.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("secure");
        _library.VerifyNoOtherCalls();

        var (page, link, _) = await RequestEmailAsync(browser, press.Headers.Location.OriginalString, "add-by-link@test.local");

        Decoded(page).Should().Contain($"Sign in to add {RecallWebApplicationFactory.KnownSeriesName} to your library.")
            .And.Contain("You'll get a notification when new episodes air. Free, no password.");
        link.Should().NotContain("returnUrl", "the return address is on the token");

        var signIn = await browser.GetAsync(link);

        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signIn.Headers.Location!.OriginalString.Should().Be($"/Series/Details/{SeriesId}");
        SetCookies(signIn).Should().Contain(c => c.StartsWith("Recall.Auth="));
        SetCookies(signIn).Should().Contain(c => c.StartsWith(PendingActions.CookieName + "=;"), "the action is done once, and the cookie removed");

        var userId = await UserIdAsync("add-by-link@test.local");
        _library.Verify(x => x.AddAsync(userId, SeriesId, It.IsAny<CancellationToken>()), Times.Once);

        var landed = Decoded(await browser.GetStringAsync(signIn.Headers.Location.OriginalString));
        landed.Should().Contain($"Added {RecallWebApplicationFactory.KnownSeriesName} to your library.");
        landed.Should().Contain($"action=\"/Series/Details/{SeriesId}?handler=UndoAdd\"", "the toast offers Undo");

        Counted().Should().Contain(new Dictionary<string, long>
        {
            ["action.clicked.add-series"] = 1,
            ["view.login.anon"] = 1,
            [SiteMetricKeys.LinkSent] = 1,
            [SiteMetricKeys.LinkRedeemed] = 1,
            [SiteMetricKeys.NewAccount] = 1,
            ["action.completed.add-series"] = 1
        });
    }

    [Test]
    public async Task AddToWatchlist_Should_BeDoneAfterTheCode_ReturningToTheMovie()
    {
        using var browser = Browser();

        var press = await PressAsync(browser, $"/Movies/Details/{MovieId}", "ToggleWatchlist");
        var (page, _, code) = await RequestEmailAsync(browser, press.Headers.Location!.OriginalString, "watchlist-by-code@test.local");
        Decoded(page).Should().Contain($"Sign in to add {MovieName} to your watchlist.");

        var inbox = await browser.GetStringAsync("/Account/Login");
        var signIn = await browser.PostAsync("/Account/Login?handler=VerifyCode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "watchlist-by-code@test.local",
            ["code"] = code,
            ["__RequestVerificationToken"] = Antiforgery(inbox)
        }));

        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signIn.Headers.Location!.OriginalString.Should().Be($"/Movies/Details/{MovieId}");

        var userId = await UserIdAsync("watchlist-by-code@test.local");
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().TrackedMovies.AnyAsync(m => m.UserId == userId && m.TvdbId == MovieId))
                .Should().BeTrue();

        var landed = Decoded(await browser.GetStringAsync(signIn.Headers.Location.OriginalString));
        landed.Should().Contain($"Added {MovieName} to your watchlist.").And.Contain("handler=UndoWatchlist");
        landed.Should().Contain("aria-label=\"On your watchlist\"");

        Counted().Should().Contain(new Dictionary<string, long>
        {
            ["action.clicked.watchlist-movie"] = 1,
            [SiteMetricKeys.CodeRedeemed] = 1,
            ["action.completed.watchlist-movie"] = 1
        });
    }

    [Test]
    public async Task ARating_Should_KeepItsValue_AndBeSaved()
    {
        using var browser = Browser();

        var press = await PressAsync(browser, $"/Series/Details/{SeriesId}", "RateSeries", new() { ["value"] = "9" });
        var (page, link, _) = await RequestEmailAsync(browser, press.Headers.Location!.OriginalString, "rater@test.local");
        Decoded(page).Should().Contain($"Sign in to rate {RecallWebApplicationFactory.KnownSeriesName} 9 out of 10.");

        await browser.GetAsync(link);

        var userId = await UserIdAsync("rater@test.local");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserRatings.SingleAsync(r => r.UserId == userId))
            .Value.Should().Be(9);
    }

    [Test]
    public async Task TheLinkOpenedInAnotherBrowser_Should_ReturnToThePage_WithoutDoingTheAction()
    {
        using var visitor = Browser();
        using var mailApp = Browser();

        var press = await PressAsync(visitor, $"/Series/Details/{SeriesId}", "ToggleSeriesLike");
        var (_, link, _) = await RequestEmailAsync(visitor, press.Headers.Location!.OriginalString, "two-browsers@test.local");

        var signIn = await mailApp.GetAsync(link);

        signIn.Headers.Location!.OriginalString.Should().Be($"/Series/Details/{SeriesId}");
        var userId = await UserIdAsync("two-browsers@test.local");
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserLikes.AnyAsync(l => l.UserId == userId))
                .Should().BeFalse("the pending action lives in the browser it was pressed in");
        Decoded(await mailApp.GetStringAsync(signIn.Headers.Location.OriginalString)).Should().NotContain($"Liked {RecallWebApplicationFactory.KnownSeriesName}.");
        Counted().Should().NotContainKey("action.completed.like-series");
    }

    [Test]
    public async Task TheSignInPage_Should_BeUnchanged_WithoutAPendingAction_OrWithAForgedOne()
    {
        using var browser = Browser();
        Decoded(await browser.GetStringAsync("/Account/Login")).Should().NotContain("signInContext").And.Contain("<h1 class=\"mb-2\">Sign in</h1>");

        using var forger = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.Add("Cookie", $"{PendingActions.CookieName}=v1|AddSeries|{SeriesId}||0");
        (await (await forger.SendAsync(request)).Content.ReadAsStringAsync()).Should().NotContain("signInContext");
    }

    [Test]
    public async Task AReturnAddressOffThisSite_Should_NotBeFollowed_FromTheFormOrTheLink()
    {
        using var browser = Browser();
        var (_, link, _) = await RequestEmailAsync(browser, "/Account/Login?returnUrl=https%3A%2F%2Fevil.example%2F", "evil-return@test.local");

        using (var scope = _factory.Services.CreateScope())
        {
            var userId = await UserIdAsync("evil-return@test.local");
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().LoginTokens.SingleAsync(t => t.UserId == userId))
                .ReturnUrl.Should().BeNull("only a local path is stored");
        }

        var signIn = await browser.GetAsync(link + "&returnUrl=https%3A%2F%2Fevil.example%2F");

        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signIn.Headers.Location!.OriginalString.Should().Be("/Dashboard", "the link's own returnUrl is not read any more");
    }

    [Test]
    public async Task ThePressOfAVisitor_Should_ChangeNothing_UntilTheySignIn()
    {
        using var browser = Browser();

        await PressAsync(browser, $"/Movies/Details/{MovieId}", "ToggleMovieWatched");
        await PressAsync(browser, $"/Movies/Details/{MovieId}", "ToggleMovieLike");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.UserMovieWatches.CountAsync()).Should().Be(0);
        (await db.UserLikes.CountAsync(l => l.TargetType == LikeTargetType.Movie)).Should().Be(0);
        Counted().Should().Contain(new Dictionary<string, long>
        {
            ["action.clicked.watched-movie"] = 1,
            ["action.clicked.like-movie"] = 1
        });
    }
}

/// <summary>The two paths of the sign-up funnel, and "Check your inbox" after a press.</summary>
[TestFixture]
[NonParallelizable]
public sealed class SignUpPathPipelineTests : PendingActionPipelineFixture
{
    [Test]
    public async Task TheActionPath_Should_CountEachStepOncePerPress_AndTheDirectPathApart()
    {
        using var visitor = Browser();
        var press = await PressAsync(visitor, $"/Series/Details/{SeriesId}", "ToggleSeriesLike");
        await visitor.GetStringAsync(press.Headers.Location!.OriginalString);       // a second look at the sign-in page
        var (_, link, _) = await RequestEmailAsync(visitor, press.Headers.Location.OriginalString, "path-counter@test.local");
        await visitor.GetAsync(link);

        using var direct = Browser();
        await RequestEmailAsync(direct, "/Account/Login", "direct-counter@test.local");

        Counted().Should().Contain(new Dictionary<string, long>
        {
            ["action.clicked.like-series"] = 1,
            ["view.login.anon"] = 3,
            [SiteMetricKeys.SignInPageWithAction] = 1,          // two views after the press, one step
            [SiteMetricKeys.SignInPageDirect] = 1,
            [SiteMetricKeys.LinkSentWithAction] = 1,
            [SiteMetricKeys.LinkSentDirect] = 1,
            [SiteMetricKeys.LinkSent] = 2,
            [SiteMetricKeys.SignedInWithAction] = 1,
            [SiteMetricKeys.NewAccountWithAction] = 1,
            [SiteMetricKeys.ActionDone] = 1
        });
        Counted().Should().NotContainKey(SiteMetricKeys.SignedInDirect);
    }
}
