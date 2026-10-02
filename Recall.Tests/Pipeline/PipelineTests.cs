using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Recall.Tests.Pipeline;

/// <summary>
/// A few requests through the whole application, for the things unit tests
/// can't see: which pages are public, where a protected one sends you, and
/// that the antiforgery check is really in front of the POST handlers.
/// </summary>
[TestFixture]
public sealed class PipelineTests
{
    private RecallWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartApplication()
    {
        _factory = new RecallWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    [OneTimeTearDown]
    public void StopApplication()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task AnonymousVisitor_Should_SeeASeriesDetailsPage()
    {
        var response = await _client.GetAsync($"/Series/Details/{RecallWebApplicationFactory.KnownSeriesId}");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain(RecallWebApplicationFactory.KnownSeriesName);
        html.Should().Contain("Pilot Episode", "the episode list renders for a signed-out visitor");
        html.Should().Contain("index, follow", "the page opts in to search indexing");
        html.Should().Contain("TheTVDB").And.Contain("https://thetvdb.com", "the attribution is in the shared layout");
        html.Should().NotContain("handler=ToggleLibrary", "a signed-out visitor is offered no way to change anything");
        html.Should().NotContain("handler=MarkSeasonWatched");
        html.Should().Contain("Sign in to track this series", "a visitor gets the sign-in card in place of the action row");
        html.Should().Contain("/Account/Login?returnUrl=");
    }

    [Test]
    public async Task ASeriesTheTvDbDoesNotKnow_Should_Be404()
    {
        var response = await _client.GetAsync("/Series/Details/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestCase("/Dashboard")]
    [TestCase("/Library")]
    [TestCase("/Search")]
    [TestCase("/Account/Profile")]
    [TestCase("/Account/Notifications")]
    [TestCase("/Admin")]
    public async Task ProtectedPage_Should_RedirectAnAnonymousVisitorToLogin(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/Account/Login");
        response.Headers.Location.Query.Should().Be($"?ReturnUrl={Uri.EscapeDataString(path)}");
    }

    [Test]
    public async Task PublicPages_Should_RenderWithoutSigningIn()
    {
        foreach (var path in new[] { "/", "/Account/Login", "/Privacy" })
        {
            var response = await _client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} is public");
        }
    }

    [Test]
    public async Task Post_WithoutAnAntiforgeryToken_Should_BeRejected()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "alice@test.local" });

        var response = await _client.PostAsync("/Account/Login", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Post_ToADetailsHandler_WithoutAnAntiforgeryToken_Should_BeRejected_BeforeTheHandlerRuns()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["value"] = "8" });

        var response = await _client.PostAsync(
            $"/Series/Details/{RecallWebApplicationFactory.KnownSeriesId}?handler=RateSeries", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a public page's handlers are reachable by anyone, so the token check is what stops a forged form");
    }

    [Test]
    public async Task Logout_ByGet_Should_ChangeNothing_AndJustGoHome()
    {
        var response = await _client.GetAsync("/Account/Logout");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a GET must not sign anyone out");
    }

    [Test]
    public async Task AnUnknownAddress_Should_Be404_WithAPageInsideTheLayout()
    {
        var response = await _client.GetAsync("/this-page-does-not-exist");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        html.Should().Contain("Page not found");
        html.Should().Contain("<nav", "the status page is rendered inside the normal layout");
        html.Should().Contain("Metadata provided by");
    }

    [Test]
    public async Task ASeriesTheTvDbDoesNotKnow_Should_GetTheNotFoundPage()
    {
        var response = await _client.GetAsync("/Series/Details/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Page not found");
    }

    [Test]
    public async Task ARejectedPost_Should_KeepItsStatus_AndExplainItself()
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "someone@example.com" });

        var response = await _client.PostAsync("/Account/Login", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("go through", "the rejected form gets a page that explains what happened");
    }

    [Test]
    public async Task ReloadingTheSignInPage_Should_NeverBeRateLimited()
    {
        // Well past the eight link requests the policy allows per five minutes.
        for (var i = 0; i < 30; i++)
        {
            var response = await _client.GetAsync("/Account/Login");
            response.StatusCode.Should().Be(HttpStatusCode.OK, "page loads are not requests for a link (load {0})", i + 1);
        }
    }

    [Test]
    public async Task SignInPage_Should_PutTheFormFirst_AndUseItsOwnValidationWords()
    {
        var html = await (await _client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        html.Should().Contain("No password needed.");
        html.Should().Contain("data-val-email=\"Enter a valid email address.\"");
        html.IndexOf("<form", StringComparison.Ordinal).Should().BeLessThan(
            html.IndexOf("tvdb-benefits", StringComparison.Ordinal), "the form comes before the list of benefits");
    }

    [Test]
    public async Task PrivacyPage_Should_NameWhatIsStoredInTheBrowser_AndTheRetentionPeriods()
    {
        var html = await (await _client.GetAsync("/Privacy")).Content.ReadAsStringAsync();

        html.Should().Contain("Recall.Auth");
        html.Should().Contain(".AspNetCore.Antiforgery");
        html.Should().Contain("recall.cookie-notice-dismissed");
        html.Should().Contain("deleted 7 days after they expired or were used");
        html.Should().Contain("artworks.thetvdb.com");
    }

    [Test]
    public async Task ErrorPage_Should_Apologise_WithoutDevelopmentText()
    {
        var response = await _client.GetAsync("/Error");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("Something went wrong");
        html.Should().NotContain("Development");
        html.Should().NotContain("ASPNETCORE_ENVIRONMENT");
    }

    [Test]
    public async Task EveryPage_Should_OfferASkipLink_AndMarkTheCurrentNavigationItem()
    {
        var html = await (await _client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        html.Should().Contain("href=\"#main\"");
        html.Should().Contain("id=\"main\"");
        html.Should().Contain("aria-current=\"page\"");
        html.Should().Contain("aria-controls=\"mainNav\"").And.Contain("id=\"mainNav\"");
    }

    [Test]
    public async Task HealthEndpoints_Should_AnswerAnonymously()
    {
        (await _client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK, "the database check runs against the test database");
    }
}
