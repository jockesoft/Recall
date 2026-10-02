using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Notifications;

namespace Recall.Tests.Pages;

/// <summary>
/// Signing out and marking a notification read both change state, so neither
/// may happen on a GET (no antiforgery check, and triggerable from any site).
/// </summary>
[TestFixture]
public class PostOnlyStateChangeTests
{
    private Mock<IAuthenticationService> _authenticationService = null!;

    [SetUp]
    public void SetUp() => _authenticationService = new Mock<IAuthenticationService>();

    private LogoutModel CreateLogoutModel() => new()
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton(_authenticationService.Object)
                    .BuildServiceProvider()
            }
        }
    };

    private void VerifySignedOut(Times times) =>
        _authenticationService.Verify(
            x => x.SignOutAsync(
                It.IsAny<HttpContext>(),
                CookieAuthenticationDefaults.AuthenticationScheme,
                It.IsAny<AuthenticationProperties?>()),
            times);

    [Test]
    public void Logout_Get_Should_RedirectHome_WithoutSigningOut()
    {
        var result = CreateLogoutModel().OnGet();

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("/Index");
        VerifySignedOut(Times.Never());
    }

    [Test]
    public async Task Logout_Post_Should_SignOut_AndRedirectHome()
    {
        var result = await CreateLogoutModel().OnPostAsync();

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().Be("/Index");
        VerifySignedOut(Times.Once());
    }

    [Test]
    public async Task Notifications_OpenPost_Should_MarkRead_AndRedirectToTheTarget()
    {
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(userId);
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.OpenAsync(userId, notificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("/Episodes/Details/123");
        var sut = new NotificationsModel(currentUser.Object, notifications.Object, Mock.Of<ITheTvDbService>(), NullLogger<NotificationsModel>.Instance);

        var result = await sut.OnPostOpenAsync(notificationId, CancellationToken.None);

        result.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be("/Episodes/Details/123");
        notifications.Verify(x => x.OpenAsync(userId, notificationId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Notifications_Should_HaveNoGetHandlerThatOpensOne()
    {
        // Razor Pages binds handlers by method name, so the absence of an
        // OnGetOpen* method is what makes GET ?handler=Open fall through to the
        // read-only list.
        typeof(NotificationsModel).GetMethods()
            .Should().NotContain(m => m.Name.StartsWith("OnGetOpen", StringComparison.Ordinal));
    }
}
