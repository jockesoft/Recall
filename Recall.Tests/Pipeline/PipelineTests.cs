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
    public async Task HealthEndpoints_Should_AnswerAnonymously()
    {
        (await _client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK, "the database check runs against the test database");
    }
}
