using Recall.Tests.TestSupport;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Authentication;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Services.Authentication;

/// <summary>
/// The link and the code in one sign-in email, through the real service, token
/// repository, user repository and abuse guard on SQLite. The email is read
/// back from what was queued, as a person would read it.
/// </summary>
[TestFixture]
public sealed class SignInCodeFlowTests
{
    private const string Address = "reader@test.local";

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private LoginAbuseGuard _guard = null!;
    private LoginTokenOptions _options = null!;
    private List<(string Text, string Html)> _emails = null!;

    /// <summary>What the service counted, across every service instance of a test.</summary>
    private RecordingSiteMetrics Metrics { get; set; } = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        await using (var db = new AppDbContext(_dbOptions))
            await db.Database.EnsureCreatedAsync();

        // No resend cooldown, so a test can ask for a second email at once.
        _options = new LoginTokenOptions { TokenLifetimeMinutes = 20, ResendCooldownSeconds = 0, MaxRequestsPerEmailPerDay = 50 };
        _guard = new LoginAbuseGuard(Options.Create(_options));
        _emails = [];
        Metrics = new RecordingSiteMetrics();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        _guard.Dispose();
        await _connection.DisposeAsync();
    }

    private PasswordlessAuthService Service(AppDbContext db)
    {
        var mail = new Mock<IMailService>();
        mail.Setup(x => x.QueueEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, int, CancellationToken>((_, _, text, html, _, _) => _emails.Add((text, html!)))
            .Returns(Task.CompletedTask);

        return new PasswordlessAuthService(
            new AppUserRepository(db), new LoginTokenRepository(db), mail.Object, _guard,
            Options.Create(_options), Metrics, NullLogger<PasswordlessAuthService>.Instance);
    }

    /// <summary>Requests a sign-in email and returns the link's token and the code, as they appear in it.</summary>
    private async Task<(string Token, string Code)> RequestEmailAsync(string address = Address, string? returnUrl = null)
    {
        await using var db = new AppDbContext(_dbOptions);
        string? token = null;
        await Service(db).RequestLoginAsync(address, t =>
        {
            token = t;
            return $"https://recall.test/Account/Verify?token={t}";
        }, returnUrl);

        var grouped = Regex.Match(_emails[^1].Text, @"\b(\d{3}) (\d{3})\b");
        grouped.Success.Should().BeTrue("the email shows the code in two groups of three");
        return (token!, grouped.Groups[1].Value + grouped.Groups[2].Value);
    }

    private async Task<LoginRedemptionResult> RedeemLinkAsync(string token)
    {
        await using var db = new AppDbContext(_dbOptions);
        return await Service(db).RedeemAsync(token);
    }

    private async Task<LoginRedemptionResult> RedeemCodeAsync(string? code, string address = Address)
    {
        await using var db = new AppDbContext(_dbOptions);
        return await Service(db).RedeemCodeAsync(address, code);
    }

    private static string WrongCode(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    // ---- the email ---------------------------------------------------------------

    [Test]
    public async Task TheEmail_Should_HoldTheLinkAndTheGroupedCode_WithOneLineOnWhatTheCodeIsFor_AndTheConfiguredExpiry()
    {
        var (token, code) = await RequestEmailAsync();
        var (text, html) = _emails.Single();
        var grouped = $"{code[..3]} {code[3..]}";

        foreach (var part in new[] { text, html })
        {
            part.Should().Contain(token);
            part.Should().Contain(grouped);
            part.Should().Contain("where the link doesn't open, such as Recall added to your Home Screen");
            part.Should().Contain("20 minutes", "the lifetime comes from Login:TokenLifetimeMinutes");
        }
    }

    [Test]
    public async Task OnlyAHashOfTheCode_Should_BeStored()
    {
        var (_, code) = await RequestEmailAsync();

        await using var db = new AppDbContext(_dbOptions);
        var row = await db.LoginTokens.SingleAsync();
        row.CodeHash.Should().NotBeNullOrEmpty().And.NotContain(code);
        row.CodeHash.Should().Be(SignInCode.Hash(code, row.Id));
        row.CodeAttempts.Should().Be(0);
    }

    // ---- the site metrics -----------------------------------------------------------

    [Test]
    public async Task TheMetrics_Should_CountTheEmail_EachWayIn_AndTheFirstSignInAsANewAccount_Once()
    {
        var (token, _) = await RequestEmailAsync();
        Metrics.Keys.Should().Equal(SiteMetricKeys.LinkSent);

        (await RedeemLinkAsync(token)).Succeeded.Should().BeTrue();
        Metrics.Keys.Should().Equal(
            SiteMetricKeys.LinkSent, SiteMetricKeys.LinkRedeemed, SiteMetricKeys.SignedInDirect,
            SiteMetricKeys.NewAccount, SiteMetricKeys.NewAccountDirect);

        var (_, code) = await RequestEmailAsync();
        (await RedeemCodeAsync(code)).Succeeded.Should().BeTrue();
        Metrics.Keys.Skip(5).Should().Equal(SiteMetricKeys.LinkSent, SiteMetricKeys.CodeRedeemed, SiteMetricKeys.SignedInDirect);

        await using var db = new AppDbContext(_dbOptions);
        (await db.AppUsers.SingleAsync()).FirstSignInUtc.Should().NotBeNull();
    }

    [Test]
    public async Task AnEmailAskedForWithAPendingAction_Should_PutItsSignIn_OnTheActionPath()
    {
        string? token = null;
        await using (var db = new AppDbContext(_dbOptions))
            (await Service(db).RequestLoginAsync(Address, t => { token = t; return $"https://recall.test/Account/Verify?token={t}"; },
                returnUrl: "/Series/Details/1", withPendingAction: true)).Should().BeTrue("an email was queued");

        var signedIn = await RedeemLinkAsync(token!);

        signedIn.WithPendingAction.Should().BeTrue();
        Metrics.Keys.Should().Contain(SiteMetricKeys.SignedInWithAction).And.Contain(SiteMetricKeys.NewAccountWithAction)
            .And.NotContain(SiteMetricKeys.SignedInDirect).And.NotContain(SiteMetricKeys.NewAccountDirect);
    }

    [Test]
    public async Task TheMetrics_Should_CountNothing_ForAWrongCode_OrAnUnusedLink()
    {
        var (_, code) = await RequestEmailAsync();
        await RedeemCodeAsync(WrongCode(code));
        await RedeemLinkAsync("not-a-token");

        Metrics.Keys.Should().Equal(SiteMetricKeys.LinkSent);

        await using var db = new AppDbContext(_dbOptions);
        (await db.AppUsers.SingleAsync()).FirstSignInUtc.Should().BeNull("requesting an email creates the row, signing in makes it a new account");
    }

    // ---- where the sign-in returns to ------------------------------------------------

    [Test]
    public async Task TheReturnAddress_Should_RideOnTheToken_AndComeBackFromTheLinkAndFromTheCode()
    {
        var (token, _) = await RequestEmailAsync(returnUrl: "/Series/Details/81189");
        _emails[^1].Text.Should().NotContain("Series", "the address is kept on the token, not put in the email");

        (await RedeemLinkAsync(token)).ReturnUrl.Should().Be("/Series/Details/81189");

        var (_, code) = await RequestEmailAsync(returnUrl: "/Movies/Details/1157");
        (await RedeemCodeAsync(code)).ReturnUrl.Should().Be("/Movies/Details/1157");
    }

    [TestCase("https://evil.example/")]
    [TestCase("//evil.example/Series/Details/1")]
    [TestCase("/\\evil.example")]
    public async Task AnAddressOffThisSite_Should_NeverBeStored(string returnUrl)
    {
        var (token, _) = await RequestEmailAsync(returnUrl: returnUrl);

        await using (var db = new AppDbContext(_dbOptions))
            (await db.LoginTokens.SingleAsync()).ReturnUrl.Should().BeNull();

        (await RedeemLinkAsync(token)).ReturnUrl.Should().BeNull();
    }

    [Test]
    public async Task AnAddressOffThisSite_Should_BeDroppedAgainOnRedemption_ShouldTheRowEverHoldOne()
    {
        var (token, code) = await RequestEmailAsync(returnUrl: "/Series/Details/1");
        await using (var db = new AppDbContext(_dbOptions))
            await db.LoginTokens.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReturnUrl, "https://evil.example/"));

        var signedIn = await RedeemCodeAsync(code);

        signedIn.Succeeded.Should().BeTrue();
        signedIn.ReturnUrl.Should().BeNull("the redemption checks the address again");
        _ = token;
    }

    // ---- either way in, once ------------------------------------------------------

    [Test]
    public async Task TheCode_Should_SignIn_AndThenTheLinkShouldNoLongerWork()
    {
        var (token, code) = await RequestEmailAsync();

        var byCode = await RedeemCodeAsync(code);

        byCode.Succeeded.Should().BeTrue();
        byCode.Email.Should().Be(Address);
        (await RedeemLinkAsync(token)).Succeeded.Should().BeFalse("the link and the code are one token, used once");
        (await RedeemCodeAsync(code)).Succeeded.Should().BeFalse();
    }

    [Test]
    public async Task TheLink_Should_SignIn_AndThenTheCodeShouldNoLongerWork()
    {
        var (token, code) = await RequestEmailAsync();

        (await RedeemLinkAsync(token)).Succeeded.Should().BeTrue();

        (await RedeemCodeAsync(code)).Status.Should().Be(LoginRedemptionStatus.InvalidOrExpired);
    }

    [TestCase("{0} {1}")]
    [TestCase("  {0}{1} ")]
    [TestCase("{0} {1}")]
    [TestCase(" {0} {1}\t")]
    public async Task SpacesInTheCode_Should_BeIgnored(string format)
    {
        var (_, code) = await RequestEmailAsync();

        var result = await RedeemCodeAsync(string.Format(format, code[..3], code[3..]));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task TheAddress_Should_MatchWhateverItsCase()
    {
        var (_, code) = await RequestEmailAsync();

        (await RedeemCodeAsync(code, "  Reader@Test.LOCAL ")).Succeeded.Should().BeTrue();
    }

    // ---- one answer for every failure --------------------------------------------------

    [Test]
    public async Task AnExpiredCode_Should_NotSignIn()
    {
        var (_, code) = await RequestEmailAsync();
        await using (var db = new AppDbContext(_dbOptions))
            await db.LoginTokens.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresUtc, DateTime.UtcNow.AddSeconds(-1)));

        (await RedeemCodeAsync(code)).Status.Should().Be(LoginRedemptionStatus.InvalidOrExpired);
    }

    [Test]
    public async Task AWrongCode_AnExpiredOne_AndAnUnknownAddress_Should_AllGetTheSameAnswer()
    {
        var (_, code) = await RequestEmailAsync();
        await RequestEmailAsync("expired@test.local");
        var expiredCode = Regex.Match(_emails[^1].Text, @"\b(\d{3}) (\d{3})\b").Value.Replace(" ", "");
        await using (var db = new AppDbContext(_dbOptions))
            await db.LoginTokens.Where(x => x.User.Email == "expired@test.local")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresUtc, DateTime.UtcNow.AddSeconds(-1)));

        var wrong = await RedeemCodeAsync(WrongCode(code));
        var expired = await RedeemCodeAsync(expiredCode, "expired@test.local");
        var unknown = await RedeemCodeAsync(code, "nobody@test.local");
        var malformed = await RedeemCodeAsync("12ab56");

        new[] { wrong, expired, unknown, malformed }.Should().AllBeEquivalentTo(LoginRedemptionResult.Invalid());
    }

    // ---- five wrong codes ---------------------------------------------------------------

    [Test]
    public async Task TheFifthWrongCode_Should_InvalidateTheToken_SoNeitherTheCodeNorTheLinkWorks()
    {
        var (token, code) = await RequestEmailAsync();

        for (var i = 1; i < SignInCode.MaxWrongAttempts; i++)
            (await RedeemCodeAsync(WrongCode(code))).Status.Should().Be(LoginRedemptionStatus.InvalidOrExpired, "attempt {0}", i);

        (await RedeemCodeAsync(WrongCode(code))).Status.Should().Be(LoginRedemptionStatus.TooManyAttempts);

        (await RedeemCodeAsync(code)).Status.Should().Be(LoginRedemptionStatus.TooManyAttempts, "even the right code is too late");
        (await RedeemLinkAsync(token)).Succeeded.Should().BeFalse("the token is invalidated, link included");
        await using var db = new AppDbContext(_dbOptions);
        var row = await db.LoginTokens.SingleAsync();
        row.ConsumedUtc.Should().NotBeNull();
        row.CodeAttempts.Should().Be(SignInCode.MaxWrongAttempts);
    }

    [Test]
    public async Task ANewEmail_Should_GiveANewCodeAndAFreshAllowance()
    {
        var (_, first) = await RequestEmailAsync();
        for (var i = 0; i < SignInCode.MaxWrongAttempts; i++)
            await RedeemCodeAsync(WrongCode(first));

        var (_, second) = await RequestEmailAsync();

        (await RedeemCodeAsync(second)).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task AnUnknownAddress_Should_BeLockedOutAfterFiveTries_JustLikeAKnownOne()
    {
        var results = new List<LoginRedemptionStatus>();
        for (var i = 0; i < SignInCode.MaxWrongAttempts + 1; i++)
            results.Add((await RedeemCodeAsync("123456", "nobody@test.local")).Status);

        results.Should().Equal(
            LoginRedemptionStatus.InvalidOrExpired, LoginRedemptionStatus.InvalidOrExpired, LoginRedemptionStatus.InvalidOrExpired,
            LoginRedemptionStatus.InvalidOrExpired, LoginRedemptionStatus.TooManyAttempts, LoginRedemptionStatus.TooManyAttempts);
        (await new AppDbContext(_dbOptions).AppUsers.AnyAsync()).Should().BeFalse("trying a code never creates an account");
    }
}
