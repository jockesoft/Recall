using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Push;

namespace Recall.Tests.Pipeline;

/// <summary>
/// Profile's push section through the real pipeline, signed in, with push
/// enabled: the subscribe form is behind the antiforgery token like every
/// other form, and the page never asks the browser for permission by itself.
/// </summary>
[TestFixture]
public sealed class PushPipelineTests
{
    private const string UserHeader = "X-Test-User";
    private const string AndroidChrome =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";

    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string P256dh = Base64Url([0x04, .. new byte[64]]);
    private static readonly string Auth = Base64Url(new byte[16]);

    private RecallWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartApplicationAsync()
    {
        var keys = VapidKeys.Generate();
        _baseFactory = new RecallWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Push:Enabled", "true");
            builder.UseSetting("Push:PublicKey", keys.PublicKey);
            builder.UseSetting("Push:PrivateKey", keys.PrivateKey);
            builder.UseSetting("Push:Subject", "mailto:admin@recall.nu");
            builder.ConfigureTestServices(services =>
            {
                // A request with the header is signed in as that user; one without stays anonymous.
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.Scheme, _ => { });
                services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderAuthenticationHandler.Scheme);
            });
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        _client.DefaultRequestHeaders.Add(UserHeader, UserId.ToString());
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(AndroidChrome);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AppUsers.Add(new AppUserEntity { Id = UserId, Username = "push-user", Email = "push@test.local" });
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
    public async Task Subscribe_Should_BeRejectedWithoutTheAntiforgeryToken_AndStoredWithIt()
    {
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var fields = new Dictionary<string, string>
        {
            ["endpoint"] = endpoint, ["p256dh"] = P256dh, ["auth"] = Auth, ["timeZone"] = "Europe/Stockholm"
        };

        var forged = await _client.PostAsync("/Account/Profile?handler=SubscribeDevice", new FormUrlEncodedContent(fields));

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DevicesAsync()).Should().NotContain(d => d.Endpoint == endpoint);

        fields["__RequestVerificationToken"] = await AntiforgeryTokenAsync();
        var real = await _client.PostAsync("/Account/Profile?handler=SubscribeDevice", new FormUrlEncodedContent(fields));

        real.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var stored = (await DevicesAsync()).Single(d => d.Endpoint == endpoint);
        stored.DeviceLabel.Should().Be("Chrome on Android");
        stored.TimeZone.Should().Be("Europe/Stockholm");
        stored.UserId.Should().Be(UserId);
    }

    [Test]
    public async Task UnsubscribeAndRemove_Should_AlsoNeedTheToken()
    {
        var unsubscribe = await _client.PostAsync("/Account/Profile?handler=UnsubscribeDevice",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["endpoint"] = "https://fcm.googleapis.com/fcm/send/x" }));
        var remove = await _client.PostAsync($"/Account/Profile?handler=RemoveDevice&id={Guid.NewGuid()}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        var quiet = await _client.PostAsync("/Account/Profile?handler=SetQuietHours",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["on"] = "false" }));

        unsubscribe.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        remove.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        quiet.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ProfilePage_Should_OfferTheSwitch_WithoutAskingForPermissionOnLoad()
    {
        var html = await _client.GetStringAsync("/Account/Profile");

        html.Should().Contain("data-push ").And.Contain("id=\"pushSwitchOn\"");
        html.Should().MatchRegex("<script src=\"/js/tvdb-push(\\.[a-z0-9]+)?\\.js");
        html.Should().NotContain("requestPermission", "the page itself never asks; only the script's button handler does");
    }

    [Test]
    public async Task PushScript_Should_AskForPermission_OnlyFromTheTurnOnButtonsClickHandler()
    {
        var script = await _client.GetStringAsync("/js/tvdb-push.js");

        Regex.Matches(script, @"\brequestPermission\b").Should().HaveCount(1, "one place asks");

        // That place is inside turnOn() ...
        var turnOn = script.IndexOf("async function turnOn()", StringComparison.Ordinal);
        var nextFunction = script.IndexOf("async function ", turnOn + 1, StringComparison.Ordinal);
        var ask = script.IndexOf("requestPermission", StringComparison.Ordinal);
        turnOn.Should().BeGreaterThan(0);
        ask.Should().BeInRange(turnOn, nextFunction);

        // ... and turnOn() is only ever called by the button's click listener, not on load.
        Regex.Matches(script, @"\bturnOn\b").Select(m => script.Substring(Math.Max(0, m.Index - 60), Math.Min(m.Index, 60) + m.Length))
            .Should().HaveCount(2)
            .And.ContainSingle(s => s.Contains("async function turnOn"))
            .And.ContainSingle(s => s.Contains("getElementById(\"pushSwitchOn\").addEventListener(\"click\", turnOn"));
    }

    private async Task<string> AntiforgeryTokenAsync()
    {
        var html = await _client.GetStringAsync("/Account/Profile");
        var form = html[html.IndexOf("id=\"pushSubscribeForm\"", StringComparison.Ordinal)..];
        return Regex.Match(form, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    private async Task<List<PushSubscriptionEntity>> DevicesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PushSubscriptions.AsNoTracking().ToListAsync();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers[UserHeader], out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = RecallPrincipal.Create(userId, "push-user", "push@test.local", UserRole.Admin);
            var identity = new ClaimsIdentity(principal.Claims, Scheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }
}
