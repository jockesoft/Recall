using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Digest;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Pages;

/// <summary>The digest's unsubscribe link (a page with a button) and its one-click header endpoint.</summary>
[TestFixture]
public class DigestUnsubscribeTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private DigestUnsubscribeTokens _tokens = null!;
    private Mock<IAppUserRepository> _users = null!;
    private UnsubscribeModel _page = null!;
    private OneClickModel _oneClick = null!;

    [SetUp]
    public void SetUp()
    {
        _tokens = new DigestUnsubscribeTokens(new EphemeralDataProtectionProvider());
        _users = new Mock<IAppUserRepository>();
        _users.Setup(x => x.SetDigestOptInAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _page = new UnsubscribeModel(_tokens, _users.Object, NullLogger<UnsubscribeModel>.Instance).WithTempData();
        _oneClick = new OneClickModel(_tokens, _users.Object, NullLogger<OneClickModel>.Instance).WithTempData();
    }

    private void VerifySwitchedOff(Times times) =>
        _users.Verify(x => x.SetDigestOptInAsync(UserId, false, It.IsAny<CancellationToken>()), times);

    // ---- the link in the email: a page with a button -------------------------------

    [Test]
    public void OpeningTheLink_Should_ShowTheButton_AndChangeNothing()
    {
        _page.Token = _tokens.Create(UserId);

        _page.OnGet();

        _page.State.Should().Be(UnsubscribeModel.UnsubscribeState.Confirm);
        _users.VerifyNoOtherCalls();
    }

    [Test]
    public void ThePage_Should_HaveNoGetHandlerThatUnsubscribes()
    {
        typeof(UnsubscribeModel).GetMethods()
            .Where(m => m.Name.StartsWith("OnGet", StringComparison.Ordinal))
            .Select(m => m.ReturnType)
            .Should().Equal([typeof(void)], "a mail scanner that opens the link must not unsubscribe anyone");
    }

    [Test]
    public async Task PressingTheButton_Should_SwitchTheDigestOff_WithoutSigningIn()
    {
        _page.Token = _tokens.Create(UserId);

        var result = await _page.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _page.State.Should().Be(UnsubscribeModel.UnsubscribeState.Done);
        VerifySwitchedOff(Times.Once());
    }

    [Test]
    public async Task Unsubscribing_Should_BeIdempotent_AndLookTheSame_WhenTheAccountIsGone()
    {
        _users.Setup(x => x.SetDigestOptInAsync(UserId, false, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _page.Token = _tokens.Create(UserId);

        await _page.OnPostAsync(CancellationToken.None);
        await _page.OnPostAsync(CancellationToken.None);

        _page.State.Should().Be(UnsubscribeModel.UnsubscribeState.Done, "the page does not reveal whether the account exists");
    }

    [TestCase(null)]
    [TestCase("made-up")]
    public async Task ABadToken_Should_GetTheInvalidLinkPage_AndChangeNothing(string? token)
    {
        _page.Token = token;

        _page.OnGet();
        _page.State.Should().Be(UnsubscribeModel.UnsubscribeState.InvalidLink);

        await _page.OnPostAsync(CancellationToken.None);
        _page.State.Should().Be(UnsubscribeModel.UnsubscribeState.InvalidLink);
        _users.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AFailure_Should_KeepTheButton_AndSaySo()
    {
        _users.Setup(x => x.SetDigestOptInAsync(UserId, false, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        _page.Token = _tokens.Create(UserId);

        await _page.OnPostAsync(CancellationToken.None);

        _page.State.Should().Be(UnsubscribeModel.UnsubscribeState.Confirm);
        _page.ErrorToast().Should().StartWith("Could not turn the weekly email off");
    }

    // ---- List-Unsubscribe-Post: one click from the mail client ---------------------

    [Test]
    public async Task OneClick_Post_Should_SwitchTheDigestOff()
    {
        var result = await _oneClick.OnPostAsync(_tokens.Create(UserId), CancellationToken.None);

        result.Should().BeOfType<ContentResult>().Which.Content.Should().Be("Unsubscribed.");
        VerifySwitchedOff(Times.Once());
    }

    [Test]
    public async Task OneClick_Post_Should_RefuseABadToken()
    {
        var result = await _oneClick.OnPostAsync("made-up", CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
        _users.VerifyNoOtherCalls();
    }

    [Test]
    public void OneClick_Get_Should_ChangeNothing_AndSendAPersonToTheButtonPage()
    {
        var token = _tokens.Create(UserId);

        var result = _oneClick.OnGet(token);

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Digest/Unsubscribe");
        redirect.RouteValues.Should().Contain("token", token);
        _users.VerifyNoOtherCalls();
    }

    [Test]
    public void OnlyTheOneClickPage_Should_BeExemptFromAntiforgery()
    {
        typeof(OneClickModel).GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), inherit: true)
            .Should().ContainSingle("a mail client posts here with no cookie and no form of ours");
        typeof(UnsubscribeModel).GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), inherit: true)
            .Should().BeEmpty("the button on the page is an ordinary form, behind the antiforgery token");
    }
}
