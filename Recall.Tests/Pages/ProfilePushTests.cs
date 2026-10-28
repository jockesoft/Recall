using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Pages;

/// <summary>
/// Profile's Notifications section: the device switch, the device list with
/// its Remove buttons, and the quiet-hours switch, all hidden while push is off.
/// </summary>
[TestFixture]
public class ProfilePushTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private const string AndroidChrome =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";

    // A real-looking subscription: a 65-byte key and a 16-byte secret, base64url.
    private static readonly string P256dh = Convert.ToBase64String([0x04, .. new byte[64]]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly string Auth = Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";

    private Mock<IPushSubscriptionRepository> _subscriptions = null!;
    private Mock<IAppUserRepository> _users = null!;
    private PushOptions _pushOptions = null!;

    [SetUp]
    public void SetUp()
    {
        _subscriptions = new Mock<IPushSubscriptionRepository>();
        _users = new Mock<IAppUserRepository>();
        _pushOptions = new PushOptions { Enabled = true, PublicKey = "public-key" };
    }

    private ProfileModel CreateSut(string? userAgent = AndroidChrome)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        var favorites = new Mock<IFavoritesService>();
        favorites.Setup(x => x.GetLikedTitlesAsync(UserId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FavoriteTitle>());
        var stats = new Mock<IStatsService>();
        stats.Setup(x => x.GetAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(UserStats.Empty);

        var page = new ProfileModel(
            currentUser.Object,
            stats.Object,
            favorites.Object,
            Mock.Of<ILikeRepository>(),
            Mock.Of<IWatchlistImportRepository>(),
            _users.Object,
            Options.Create(new DigestOptions()),
            _subscriptions.Object,
            Options.Create(_pushOptions),
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ProfileModel>.Instance).WithHttpContext().WithTempData();

        if (userAgent is not null)
            page.Request.Headers.UserAgent = userAgent;
        return page;
    }

    // ---- the page ------------------------------------------------------------------

    [Test]
    public async Task Get_Should_ListTheUsersDevices_WithLabelTimeZoneAndDate_AndTheQuietHoursSetting()
    {
        _subscriptions.Setup(x => x.GetForUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Device("Chrome on Android", "Europe/Stockholm", new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc), Endpoint),
            Device("Safari on iPhone", "America/New_York", new DateTime(2025, 12, 24, 9, 0, 0, DateTimeKind.Utc), "https://web.push.apple.com/xyz")
        ]);
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUserEntity { Id = UserId, PushQuietHours = false });
        var sut = CreateSut();

        await sut.OnGetAsync(CancellationToken.None);

        sut.PushAvailable.Should().BeTrue();
        sut.PushPublicKey.Should().Be("public-key");
        sut.PushQuietHours.Should().BeFalse();
        sut.PushDevices.Select(d => (d.Label, d.TimeZone, d.Added)).Should().Equal(
            ("Chrome on Android", "Europe/Stockholm", "Sat, Oct 3"),
            ("Safari on iPhone", "America/New_York", "Dec 24, 2025"));
        sut.PushDevices[0].EndpointHash.Should().MatchRegex("^[0-9a-f]{64}$", "the page carries a hash of the endpoint, not the endpoint");
        sut.PushDevices[0].EndpointHash.Should().NotContain("fcm");
    }

    [Test]
    public async Task Get_Should_ShowNothingAboutPush_AndReadNoDevices_WhilePushIsOff()
    {
        _pushOptions.Enabled = false;
        var sut = CreateSut();

        await sut.OnGetAsync(CancellationToken.None);

        sut.PushAvailable.Should().BeFalse();
        sut.PushDevices.Should().BeEmpty();
        _subscriptions.VerifyNoOtherCalls();
    }

    // ---- subscribing ----------------------------------------------------------------

    [Test]
    public async Task Subscribe_Should_StoreTheBrowser_WithALabelFromItsUserAgent_AndItsTimeZone()
    {
        var sut = CreateSut();

        var result = await sut.OnPostSubscribeDeviceAsync(Endpoint, P256dh, Auth, "Europe/Stockholm", CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _subscriptions.Verify(x => x.SaveAsync(UserId,
            new NewPushSubscription(Endpoint, P256dh, Auth, "Chrome on Android", "Europe/Stockholm"),
            It.IsAny<CancellationToken>()), Times.Once);
        sut.SuccessToast().Should().Be("Notifications are on for this device.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Mars/Olympus_Mons")]
    public async Task Subscribe_Should_FallBackToUtc_ForATimeZoneTheServerDoesNotKnow(string? zone)
    {
        await CreateSut().OnPostSubscribeDeviceAsync(Endpoint, P256dh, Auth, zone, CancellationToken.None);

        _subscriptions.Verify(x => x.SaveAsync(UserId,
            It.Is<NewPushSubscription>(s => s.TimeZone == "UTC"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("https://169.254.169.254/latest/meta-data")]
    [TestCase("http://fcm.googleapis.com/fcm/send/abc")]
    [TestCase("https://fcm.googleapis.com.evil.example/x")]
    [TestCase("https://localhost/push")]
    public async Task Subscribe_Should_RefuseAnEndpointThatIsNotAKnownPushService(string endpoint)
    {
        var sut = CreateSut();

        await sut.OnPostSubscribeDeviceAsync(endpoint, P256dh, Auth, "UTC", CancellationToken.None);

        _subscriptions.VerifyNoOtherCalls();
        sut.ErrorToast().Should().Contain("can't be turned on here");
    }

    [Test]
    public async Task Subscribe_Should_RefuseMalformedKeys()
    {
        var sut = CreateSut();

        await sut.OnPostSubscribeDeviceAsync(Endpoint, "short", Auth, "UTC", CancellationToken.None);

        _subscriptions.VerifyNoOtherCalls();
        sut.ErrorToast().Should().NotBeNull();
    }

    [Test]
    public async Task Subscribe_Should_DoNothing_WhilePushIsOff()
    {
        _pushOptions.Enabled = false;

        await CreateSut().OnPostSubscribeDeviceAsync(Endpoint, P256dh, Auth, "UTC", CancellationToken.None);

        _subscriptions.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Subscribe_Should_SayItFailed_WhenSavingThrows()
    {
        _subscriptions.Setup(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<NewPushSubscription>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var sut = CreateSut();

        await sut.OnPostSubscribeDeviceAsync(Endpoint, P256dh, Auth, "UTC", CancellationToken.None);

        sut.ErrorToast().Should().StartWith("Could not turn on notifications");
    }

    // ---- unsubscribing and removing ---------------------------------------------------

    [Test]
    public async Task Unsubscribe_Should_RemoveThisBrowsersSubscription()
    {
        var sut = CreateSut();

        await sut.OnPostUnsubscribeDeviceAsync(Endpoint, CancellationToken.None);

        _subscriptions.Verify(x => x.RemoveByEndpointAsync(UserId, Endpoint, It.IsAny<CancellationToken>()), Times.Once);
        sut.SuccessToast().Should().Be("Notifications are off for this device.");
    }

    [Test]
    public async Task Remove_Should_RemoveTheChosenDevice()
    {
        var device = Guid.NewGuid();
        _subscriptions.Setup(x => x.RemoveAsync(UserId, device, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = CreateSut();

        await sut.OnPostRemoveDeviceAsync(device, CancellationToken.None);

        sut.SuccessToast().Should().StartWith("Device removed");
    }

    [Test]
    public async Task Remove_Should_SaySo_WhenTheDeviceIsNotThereOrNotTheirs()
    {
        var sut = CreateSut();

        await sut.OnPostRemoveDeviceAsync(Guid.NewGuid(), CancellationToken.None);

        sut.InfoToast().Should().Be("That device was already removed.");
    }

    // ---- quiet hours ----------------------------------------------------------------------

    [TestCase(true, "Quiet hours are on.*08:00*")]
    [TestCase(false, "Quiet hours are off.*")]
    public async Task QuietHours_Should_BeStoredForTheUser(bool on, string toast)
    {
        var sut = CreateSut();

        await sut.OnPostSetQuietHoursAsync(on, CancellationToken.None);

        _users.Verify(x => x.SetPushQuietHoursAsync(UserId, on, It.IsAny<CancellationToken>()), Times.Once);
        sut.SuccessToast().Should().Match(toast);
    }

    private static PushDevice Device(string label, string zone, DateTime created, string endpoint) =>
        new(Guid.NewGuid(), UserId, endpoint, P256dh, Auth, label, zone, created, null, null, null);
}
