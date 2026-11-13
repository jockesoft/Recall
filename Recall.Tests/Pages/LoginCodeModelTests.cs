using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Pages.Account;
using Recall.Web.Services.Authentication;

namespace Recall.Tests.Pages;

/// <summary>
/// The sign-in page's code form: every failure the service reports as
/// invalid gets one message, and too many wrong codes give way to "request a
/// new email". (Signing in with a right code is a pipeline test: it needs the
/// cookie middleware.)
/// </summary>
[TestFixture]
public class LoginCodeModelTests
{
    private Mock<IPasswordlessAuthService> _auth = null!;

    [SetUp]
    public void SetUp() => _auth = new Mock<IPasswordlessAuthService>();

    private LoginModel CreateSut() => new LoginModel(
        _auth.Object,
        Mock.Of<ITurnstileVerifier>(),
        Options.Create(new TurnstileOptions()),
        Options.Create(new LoginTokenOptions()),
        NullLogger<LoginModel>.Instance) { Email = "reader@test.local" }.WithHttpContext();

    [Test]
    public async Task AnInvalidCode_Should_ShowTheOneMessage_AndKeepWhatWasTyped()
    {
        _auth.Setup(x => x.RedeemCodeAsync("reader@test.local", "482 914", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginRedemptionResult.Invalid());
        var sut = CreateSut();

        var result = await sut.OnPostVerifyCodeAsync("482 914", CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        sut.LinkSent.Should().BeTrue("the page stays on Check your inbox, with the field");
        sut.CodeError.Should().Be(LoginModel.WrongCodeMessage);
        sut.Code.Should().Be("482 914");
        sut.CodeLockedOut.Should().BeFalse();
    }

    [Test]
    public async Task TooManyWrongCodes_Should_SayToRequestANewEmail()
    {
        _auth.Setup(x => x.RedeemCodeAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginRedemptionResult.TooManyAttempts());
        var sut = CreateSut();

        await sut.OnPostVerifyCodeAsync("000000", CancellationToken.None);

        sut.CodeLockedOut.Should().BeTrue();
        sut.CodeError.Should().Be(LoginModel.TooManyCodesMessage).And.Contain("Request a new email");
    }

    [Test]
    public void TheMessage_Should_NotSayWhetherTheAddressHasAnAccount()
    {
        LoginModel.WrongCodeMessage.Should().NotContainAny("account", "address", "email address", "not found", "unknown");
    }
}
