using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Pages.Admin;
using Recall.Web.Services;
using Recall.Web.Services.Push;

namespace Recall.Tests.Pages;

/// <summary>Admin's "Send a test push to my devices": the admin's own devices only, and a toast that says what happened.</summary>
[TestFixture]
public class AdminTestPushTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private Mock<IPushNotificationService> _push = null!;
    private PushOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        _push = new Mock<IPushNotificationService>();
        _options = new PushOptions { Enabled = true };
    }

    private IndexModel CreateSut()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(Admin);
        return new IndexModel(
            Mock.Of<IAppUserRepository>(), currentUser.Object, _push.Object, Options.Create(_options),
            NullLogger<IndexModel>.Instance).WithTempData();
    }

    private void Result(PushRunCounts counts) =>
        _push.Setup(x => x.SendTestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(counts);

    [Test]
    public async Task TestPush_Should_GoToTheAdminsOwnDevices()
    {
        Result(new PushRunCounts(Sent: 2));
        var sut = CreateSut();

        var result = await sut.OnPostTestPushAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _push.Verify(x => x.SendTestAsync(Admin, It.IsAny<CancellationToken>()), Times.Once);
        _push.VerifyNoOtherCalls();
        sut.SuccessToast().Should().Be("Sent a test notification to 2 devices.");
    }

    [Test]
    public async Task TestPush_Should_SayHowManyFailed_AndWhichWereRemoved()
    {
        Result(new PushRunCounts(Sent: 1, Failed: 2, Removed: 1));
        var sut = CreateSut();

        await sut.OnPostTestPushAsync(CancellationToken.None);

        sut.SuccessToast().Should().Be("Sent a test notification to 1 device. 2 failed, 1 of them no longer subscribed and removed.");
    }

    [Test]
    public async Task TestPush_Should_SaySo_WhenThereAreNoDevices()
    {
        Result(PushRunCounts.None);
        var sut = CreateSut();

        await sut.OnPostTestPushAsync(CancellationToken.None);

        sut.InfoToast().Should().StartWith("You have no devices with notifications on.");
    }

    [Test]
    public async Task TestPush_Should_BeAnError_WhenEveryPushFailed()
    {
        Result(new PushRunCounts(Failed: 1));
        var sut = CreateSut();

        await sut.OnPostTestPushAsync(CancellationToken.None);

        sut.ErrorToast().Should().Be("No test notification could be sent. 1 failed.");
    }

    [Test]
    public async Task TestPush_Should_DoNothing_WhilePushIsOff()
    {
        _options.Enabled = false;
        var sut = CreateSut();

        await sut.OnPostTestPushAsync(CancellationToken.None);

        sut.PushAvailable.Should().BeFalse();
        _push.VerifyNoOtherCalls();
    }
}
