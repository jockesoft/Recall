using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Recall.Tests.TestSupport;
using Recall.Web.Services.Metrics;
using Recall.Web.Services.PendingActions;

namespace Recall.Tests.Services.PendingActions;

/// <summary>
/// The pending-action cookie: HttpOnly, signed, 15 minutes, and only the
/// allowed kinds with valid ids come back out of it.
/// </summary>
[TestFixture]
public sealed class PendingActionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    private EphemeralDataProtectionProvider _keys = null!;
    private FixedTimeProvider _clock = null!;
    private RecordingSiteMetrics _metrics = null!;
    private Web.Services.PendingActions.PendingActions _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _keys = new EphemeralDataProtectionProvider();
        _clock = new FixedTimeProvider(Now);
        _metrics = new RecordingSiteMetrics();
        _sut = new Web.Services.PendingActions.PendingActions(_keys, _metrics, _clock, NullLogger<Web.Services.PendingActions.PendingActions>.Instance);
    }

    /// <summary>Starts the action in one request and returns the cookie's Set-Cookie header.</summary>
    private SetCookieHeaderValue Started(PendingAction action, bool https = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        _sut.Start(context, action);

        return SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(v => v!).ToList())
            .Single(c => c.Name == Web.Services.PendingActions.PendingActions.CookieName);
    }

    /// <summary>A later request of the same browser, carrying the cookie's value.</summary>
    private static HttpContext WithCookie(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{Web.Services.PendingActions.PendingActions.CookieName}={value}";
        return context;
    }

    /// <summary>A value signed exactly as the app signs one, with any content: what a forger cannot make.</summary>
    private string Signed(string plain) => _keys.CreateProtector("Recall.PendingAction.v1").Protect(plain);

    private static string Ticks(DateTimeOffset at) => at.UtcTicks.ToString(CultureInfo.InvariantCulture);

    [Test]
    public void Start_Should_WriteAnHttpOnlySecureLaxCookie_ForFifteenMinutes_ThatSaysNothingInTheClear()
    {
        var cookie = Started(new PendingAction(PendingActionKind.AddSeries, 81189));

        cookie.HttpOnly.Should().BeTrue();
        cookie.Secure.Should().BeTrue();
        cookie.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        cookie.Path.ToString().Should().Be("/");
        cookie.Expires.Should().Be(Now.AddMinutes(15));
        cookie.Value.ToString().Should().NotContain("AddSeries").And.NotContain("81189");
    }

    [Test]
    public void Start_Should_CountThePress_ByKind()
    {
        Started(new PendingAction(PendingActionKind.RateEpisode, 349232, 9));

        _metrics.Keys.Should().Equal("action.clicked.rate-episode");
    }

    [Test]
    public void Start_Should_StoreAndCountNothing_ForAnInvalidAction()
    {
        var context = new DefaultHttpContext();

        _sut.Start(context, new PendingAction(PendingActionKind.RateSeries, 81189, 11));
        _sut.Start(context, new PendingAction(PendingActionKind.AddSeries, 0));
        _sut.Start(context, new PendingAction(PendingActionKind.LikeMovie, 1157, 5));
        _sut.Start(context, new PendingAction((PendingActionKind)99, 1));

        context.Response.Headers.SetCookie.Should().BeEmpty();
        _metrics.Keys.Should().BeEmpty();
    }

    [TestCase(PendingActionKind.AddSeries, null)]
    [TestCase(PendingActionKind.RateMovie, 7)]
    [TestCase(PendingActionKind.MarkEpisodeWatched, null)]
    public void Peek_Should_GiveBackTheAction_WithinFifteenMinutes(PendingActionKind kind, int? rating)
    {
        var action = new PendingAction(kind, 4242, rating);
        var value = Started(action).Value.ToString();

        _clock.Now = Now.AddMinutes(14);

        _sut.Peek(WithCookie(value)).Should().Be(action);
    }

    [Test]
    public void Peek_Should_RejectAnExpiredCookie_EvenWhenTheBrowserStillSendsIt()
    {
        var value = Started(new PendingAction(PendingActionKind.AddSeries, 81189)).Value.ToString();

        _clock.Now = Now.AddMinutes(15).AddSeconds(1);

        _sut.Peek(WithCookie(value)).Should().BeNull();
    }

    [Test]
    public void Peek_Should_RejectACookieFromTheFuture()
    {
        _sut.Read(Signed($"v1|AddSeries|81189||{Ticks(Now.AddMinutes(5))}")).Should().BeNull();
    }

    [Test]
    public void Peek_Should_RejectATamperedCookie()
    {
        var value = Started(new PendingAction(PendingActionKind.AddSeries, 81189)).Value.ToString();
        var tampered = value[..^2] + (value[^2] == 'A' ? "B" : "A") + value[^1];

        _sut.Peek(WithCookie(tampered)).Should().BeNull();
        _sut.Peek(WithCookie("v1|AddSeries|81189||" + Ticks(Now))).Should().BeNull("a value in the clear is not signed");
        _sut.Peek(WithCookie("")).Should().BeNull();
    }

    [Test]
    public void Peek_Should_RejectACookieSignedWithAnotherKeyOrPurpose()
    {
        var otherKeys = new EphemeralDataProtectionProvider();
        var elsewhere = otherKeys.CreateProtector("Recall.PendingAction.v1").Protect($"v1|AddSeries|81189||{Ticks(Now)}");
        var otherPurpose = _keys.CreateProtector("Recall.DigestUnsubscribe").Protect($"v1|AddSeries|81189||{Ticks(Now)}");

        _sut.Read(elsewhere).Should().BeNull();
        _sut.Read(otherPurpose).Should().BeNull();
    }

    [TestCase("v1|DeleteAccount|81189||{0}", TestName = "A kind that is not on the list")]
    [TestCase("v1|0|81189||{0}", TestName = "A kind by number")]
    [TestCase("v1|addseries|81189||{0}", TestName = "A kind in other case")]
    [TestCase("v1|AddSeries|0||{0}", TestName = "No id")]
    [TestCase("v1|AddSeries|-5||{0}", TestName = "A negative id")]
    [TestCase("v1|AddSeries|81189|7|{0}", TestName = "A rating on an add")]
    [TestCase("v1|RateSeries|81189||{0}", TestName = "A rating without a value")]
    [TestCase("v1|RateSeries|81189|11|{0}", TestName = "A rating above ten")]
    [TestCase("v1|RateSeries|81189|0|{0}", TestName = "A rating of nought")]
    [TestCase("v2|AddSeries|81189||{0}", TestName = "Another version")]
    [TestCase("v1|AddSeries|81189|{0}", TestName = "A missing part")]
    [TestCase("v1|AddSeries|81189|||{0}", TestName = "A part too many")]
    public void Read_Should_RejectAnythingButAnAllowedKindWithValidIds_EvenWhenSigned(string format)
    {
        _sut.Read(Signed(string.Format(CultureInfo.InvariantCulture, format, Ticks(Now)))).Should().BeNull();
    }

    [Test]
    public void Read_Should_AcceptEveryAllowedKind_WhenSignedAndValid()
    {
        foreach (var kind in Enum.GetValues<PendingActionKind>())
        {
            var action = new PendingAction(kind, 77, kind.ToString().StartsWith("Rate") ? 10 : null);
            _sut.Read(Signed($"v1|{kind}|77|{action.Rating}|{Ticks(Now)}")).Should().Be(action);
        }
    }

    [Test]
    public void Take_Should_GiveTheAction_AndRemoveTheCookie()
    {
        var action = new PendingAction(PendingActionKind.LikeSeries, 81189);
        var context = WithCookie(Started(action).Value.ToString());

        _sut.Take(context).Should().Be(action);

        var removed = SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(v => v!).ToList())
            .Single(c => c.Name == Web.Services.PendingActions.PendingActions.CookieName);
        removed.Expires.Should().BeBefore(Now, "the cookie is deleted");
    }

    [Test]
    public void Take_Should_RemoveABadCookieToo()
    {
        var context = WithCookie("garbage");

        _sut.Take(context).Should().BeNull();
        context.Response.Headers.SetCookie.ToString().Should().Contain(Web.Services.PendingActions.PendingActions.CookieName);
    }

    [TestCase(PendingActionKind.AddSeries, "/Series/Details/81189")]
    [TestCase(PendingActionKind.RateSeries, "/Series/Details/81189")]
    [TestCase(PendingActionKind.AddMovieToWatchlist, "/Movies/Details/81189")]
    [TestCase(PendingActionKind.LikeEpisode, "/Episodes/Details/81189")]
    public void TheReturnUrl_Should_BeTheDetailsPageTheActionIsAbout(PendingActionKind kind, string expected) =>
        new PendingAction(kind, 81189, kind == PendingActionKind.RateSeries ? 5 : null).ReturnUrl.Should().Be(expected);

    [Test]
    public void EveryKind_Should_HaveAMetricKeyForItsPressAndItsCompletion()
    {
        foreach (var kind in Enum.GetValues<PendingActionKind>())
        {
            SiteMetricKeys.IsKnown(SiteMetricKeys.ActionClicked(kind)).Should().BeTrue();
            SiteMetricKeys.IsKnown(SiteMetricKeys.ActionCompleted(kind)).Should().BeTrue();
            ActionLabels.For(PendingAction.Slug(kind)).Should().NotBe(PendingAction.Slug(kind), "every kind has words on the admin page");
        }
    }
}
