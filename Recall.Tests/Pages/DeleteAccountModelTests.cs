using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Account;
using Recall.Web.Services;

namespace Recall.Tests.Pages;

[TestFixture]
public class DeleteAccountModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<IAppUserRepository> _users = null!;
    private Mock<IAuthenticationService> _authentication = null!;
    private DeleteModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _users = new Mock<IAppUserRepository>();
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUserEntity { Id = UserId, Username = "saga", Email = "saga@example.com" });
        _users.Setup(x => x.DeleteAccountAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountDeletionResult.Deleted);

        _authentication = new Mock<IAuthenticationService>();

        _sut = new DeleteModel(currentUser.Object, _users.Object, NullLogger<DeleteModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddSingleton(_authentication.Object).BuildServiceProvider()
                }
            }
        }.WithTempData();
    }

    private void VerifyDeleted(Times times) =>
        _users.Verify(x => x.DeleteAccountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), times);

    private void VerifySignedOut(Times times) =>
        _authentication.Verify(
            x => x.SignOutAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<AuthenticationProperties?>()),
            times);

    // ---- the confirmation page ---------------------------------------------------

    [Test]
    public async Task Get_Should_ShowTheConfirmation_AndDeleteNothing()
    {
        var result = await _sut.OnGetAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Username.Should().Be("saga");
        _sut.Email.Should().Be("saga@example.com");
        _sut.IsOnlyAdmin.Should().BeFalse();
        VerifyDeleted(Times.Never());
    }

    [Test]
    public void ThePage_Should_HaveNoGetHandlerThatDeletes()
    {
        typeof(DeleteModel).GetMethods()
            .Where(m => m.Name.StartsWith("OnGet", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Should().Equal(["OnGetAsync"], "deleting is a POST behind the antiforgery token; the only GET shows the confirmation");
    }

    // ---- confirmation required ---------------------------------------------------

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("sagа")]            // a Cyrillic "а": looks right, is not
    [TestCase("saga2")]
    [TestCase("someone@example.com")]
    public async Task Post_Should_DeleteNothing_WithoutTheRightConfirmation(string? typed)
    {
        _sut.Confirmation = typed;

        var result = await _sut.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.ModelState[nameof(DeleteModel.Confirmation)]!.Errors.Single().ErrorMessage
            .Should().Be(DeleteModel.ConfirmationMismatchMessage);
        VerifyDeleted(Times.Never());
        VerifySignedOut(Times.Never());
    }

    [TestCase("saga")]
    [TestCase("  SAGA ")]
    [TestCase("saga@example.com")]
    [TestCase("Saga@Example.com")]
    public async Task Post_Should_Delete_SignOut_AndGoHomeWithAToast_WhenTheUsernameOrEmailIsTyped(string typed)
    {
        _sut.Confirmation = typed;

        var result = await _sut.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("/Index");
        _users.Verify(x => x.DeleteAccountAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
        VerifySignedOut(Times.Once());
        _sut.SuccessToast().Should().Be("Your account and everything in it has been deleted.");
    }

    // ---- the only admin ----------------------------------------------------------

    [Test]
    public async Task Get_Should_ExplainInsteadOfOfferingTheForm_ToTheOnlyAdmin()
    {
        _users.Setup(x => x.IsOnlyAdminAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _sut.OnGetAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.IsOnlyAdmin.Should().BeTrue();
    }

    [Test]
    public async Task Post_Should_NotEvenTry_ForTheOnlyAdmin()
    {
        _users.Setup(x => x.IsOnlyAdminAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _sut.Confirmation = "saga";

        var result = await _sut.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        VerifyDeleted(Times.Never());
        VerifySignedOut(Times.Never());
    }

    [Test]
    public async Task Post_Should_ShowTheOnlyAdminMessage_WhenTheRepositoryRefuses()
    {
        // Another admin was demoted between loading the page and pressing the button.
        _users.Setup(x => x.DeleteAccountAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountDeletionResult.OnlyAdmin);
        _sut.Confirmation = "saga";

        var result = await _sut.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.IsOnlyAdmin.Should().BeTrue();
        VerifySignedOut(Times.Never());
        _sut.SuccessToast().Should().BeNull();
    }

    // ---- failure and edge cases --------------------------------------------------

    [Test]
    public async Task Post_Should_KeepTheSession_AndSaySo_WhenTheDeletionFails()
    {
        _users.Setup(x => x.DeleteAccountAsync(UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("serialization failure"));
        _sut.Confirmation = "saga";

        var result = await _sut.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        VerifySignedOut(Times.Never());
        _sut.ErrorToast().Should().StartWith("Could not delete your account right now.");
    }

    [Test]
    public async Task Post_Should_SignOut_WhenTheAccountWasAlreadyDeletedElsewhere()
    {
        _users.Setup(x => x.DeleteAccountAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountDeletionResult.UserNotFound);
        _sut.Confirmation = "saga";

        var result = await _sut.OnPostAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("/Index");
        VerifySignedOut(Times.Once());
    }

    [Test]
    public async Task AUserWhoseRowIsGone_Should_BeSentToSignIn()
    {
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync((AppUserEntity?)null);

        (await _sut.OnGetAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Account/Login");
        (await _sut.OnPostAsync(CancellationToken.None)).Should().BeOfType<RedirectToPageResult>();
        VerifyDeleted(Times.Never());
    }
}
