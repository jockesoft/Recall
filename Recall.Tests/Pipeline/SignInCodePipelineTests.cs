using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Pages.Account;
using Recall.Web.Services.Authentication;

namespace Recall.Tests.Pipeline;

/// <summary>
/// Signing in with the code from the email through the whole application: the
/// form is behind the antiforgery token, a right code sets the sign-in cookie
/// with no other cookie of ours involved, and the link in the same email then
/// no longer works. Its own application instance, so its code attempts never
/// share a rate-limit bucket with the other pipeline tests.
/// </summary>
[TestFixture]
public sealed class SignInCodePipelineTests
{
    private RecallWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void StartApplication() => _factory = new RecallWebApplicationFactory();

    [OneTimeTearDown]
    public void StopApplication() => _factory.Dispose();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    /// <summary>Asks for a sign-in email the way the page does, and reads the link's token and the code from the queued message.</summary>
    private async Task<(string Token, string Code)> EmailFor(string address)
    {
        using var scope = _factory.Services.CreateScope();
        string? token = null;
        await scope.ServiceProvider.GetRequiredService<IPasswordlessAuthService>()
            .RequestLoginAsync(address, t => { token = t; return $"https://localhost/Account/Verify?token={t}"; });

        var body = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Emails
            .Where(e => e.ToAddress == address).OrderByDescending(e => e.CreatedUtc).Select(e => e.Body).FirstAsync();
        return (token!, Regex.Match(body, @"\b\d{3} \d{3}\b").Value);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Account/Login");
        return Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PostCodeAsync(HttpClient client, string email, string code, string? antiforgery) =>
        client.PostAsync("/Account/Login?handler=VerifyCode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["code"] = code,
            ["__RequestVerificationToken"] = antiforgery ?? string.Empty
        }));

    [Test]
    public async Task ARightCode_Should_SignIn_AndTheLinkInTheSameEmail_Should_ThenFail()
    {
        var (token, code) = await EmailFor("code-reader@test.local");
        using var client = NewClient();

        var response = await PostCodeAsync(client, "code-reader@test.local", code, await AntiforgeryTokenAsync(client));

        code.Should().MatchRegex(@"^\d{3} \d{3}$", "posted as the email shows it, with the space");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/Dashboard");
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("Recall.Auth="));

        using var other = NewClient();
        var link = await other.GetAsync($"/Account/Verify?token={Uri.EscapeDataString(token)}");
        link.StatusCode.Should().Be(HttpStatusCode.OK, "the failure page, not a redirect");
        (link.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [])
            .Should().NotContain(c => c.StartsWith("Recall.Auth="), "the code used the token up");
    }

    [Test]
    public async Task TheCodeForm_Should_BeBehindTheAntiforgeryToken()
    {
        var (_, code) = await EmailFor("forged@test.local");
        using var client = NewClient();

        var response = await PostCodeAsync(client, "forged@test.local", code, antiforgery: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task AWrongCode_AndAnUnknownAddress_Should_GetTheSamePage_WithTheFieldReadyForAnotherTry()
    {
        var (_, code) = await EmailFor("typo@test.local");
        var wrong = code.Replace(" ", "") == "000000" ? "000 001" : "000 000";
        using var client = NewClient();
        var antiforgery = await AntiforgeryTokenAsync(client);

        var known = await (await PostCodeAsync(client, "typo@test.local", wrong, antiforgery)).Content.ReadAsStringAsync();
        var unknown = await (await PostCodeAsync(client, "no-account@test.local", wrong, antiforgery)).Content.ReadAsStringAsync();

        foreach (var html in new[] { known, unknown })
        {
            WebUtility.HtmlDecode(Message(html)).Should().Be(LoginModel.WrongCodeMessage);
            html.Should().Contain("Check your inbox");
            html.Should().MatchRegex("<input[^>]*id=\"code\"[^>]*inputmode=\"numeric\"[^>]*autocomplete=\"one-time-code\"");
            html.Should().Contain("aria-invalid=\"true\"");
        }

        Message(known).Should().Be(Message(unknown), "nothing on the page says which address has an account");
    }

    private static string Message(string html) =>
        Regex.Match(html, "<p class=\"tvdb-field-message[^\"]*\" id=\"codeMessage\" role=\"alert\">([^<]*)</p>").Groups[1].Value;
}
