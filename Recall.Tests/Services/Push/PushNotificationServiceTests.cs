using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Services.Push;

namespace Recall.Tests.Services.Push;

/// <summary>
/// One push per device summarising the user's new notifications, quiet hours
/// on each device's own clock (hold, release at 08:00, drop when read), and
/// what a push service's answer does to the subscription. The sender is a
/// mock: no real push is sent.
/// </summary>
[TestFixture]
public class PushNotificationServiceTests
{
    private static readonly Guid User = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // 21:00 UTC on Sep 15: 23:00 in Stockholm (quiet), 17:00 in New York (not).
    private static readonly DateTime Evening = new(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Since = Evening.AddMinutes(-1);

    private Mock<IPushSubscriptionRepository> _subscriptions = null!;
    private Mock<INotificationRepository> _notifications = null!;
    private Mock<IAppUserRepository> _users = null!;
    private Mock<IPushSender> _sender = null!;
    private PushOptions _options = null!;
    private AppUserEntity _user = null!;
    private List<(PushTarget Target, PushPayload Payload, string Topic)> _pushed = null!;

    [SetUp]
    public void SetUp()
    {
        _subscriptions = new Mock<IPushSubscriptionRepository>();
        _notifications = new Mock<INotificationRepository>();
        _users = new Mock<IAppUserRepository>();
        _sender = new Mock<IPushSender>();
        _options = new PushOptions { Enabled = true };
        _user = new AppUserEntity { Id = User, PushQuietHours = true };
        _pushed = [];

        _users.Setup(x => x.GetByIdAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(() => _user);
        _sender
            .Setup(x => x.SendAsync(It.IsAny<PushTarget>(), It.IsAny<PushPayload>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PushTarget, PushPayload, string, CancellationToken>((target, payload, topic, _) => _pushed.Add((target, payload, topic)))
            .ReturnsAsync(PushDelivery.Delivered);
        Unread(Notice("Silo", "S02E05"), Notice("The Bear", "S04E01"), Notice("Severance", "S02E10"));
    }

    private PushNotificationService CreateSut(DateTime? now = null) => new(
        _subscriptions.Object,
        _notifications.Object,
        _users.Object,
        _sender.Object,
        Options.Create(_options),
        new FixedTimeProvider(new DateTimeOffset(now ?? Evening)),
        NullLogger<PushNotificationService>.Instance);

    private static PushDevice Device(string zone, string name = "phone", DateTime? heldSince = null, DateTime? heldUntil = null) =>
        new(Guid.NewGuid(), User, $"https://fcm.googleapis.com/fcm/send/{name}", "p256dh", "auth", "Chrome on Android", zone,
            Evening.AddDays(-30), null, heldSince, heldUntil);

    private static UnreadEpisodeNotice Notice(string series, string? code) =>
        new(Guid.NewGuid(), $"New episode of {series}", series, 900, code, 1, Since);

    private void Devices(params PushDevice[] devices) =>
        _subscriptions.Setup(x => x.GetForUserAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(devices);

    private void Unread(params UnreadEpisodeNotice[] notices) =>
        _notifications
            .Setup(x => x.GetUnreadNewEpisodesSinceAsync(User, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(notices);

    // ---- sending ---------------------------------------------------------------------

    [Test]
    public async Task NewEpisodes_Should_SendOneSummaryPushToEveryDevice_WithTheUserAsTopic()
    {
        _user.PushQuietHours = false;
        var phone = Device("Europe/Stockholm", "phone");
        var laptop = Device("America/New_York", "laptop");
        Devices(phone, laptop);

        var counts = await CreateSut().SendNewEpisodesAsync(User, Since);

        counts.Should().Be(new PushRunCounts(Sent: 2));
        _pushed.Select(p => p.Target.Endpoint).Should().Equal(phone.Endpoint, laptop.Endpoint);
        _pushed.Should().OnlyContain(p => p.Payload.Title == "3 new episodes: Silo, The Bear and 1 more"
                                          && p.Payload.Url == "/Account/Notifications"
                                          && p.Topic == User.ToString("N"));
        _subscriptions.Verify(x => x.MarkDeliveredAsync(phone.Id, Evening, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task QuietHours_Should_HoldThePushOnADeviceWhereItIsNight_AndSendItWhereItIsNot()
    {
        var stockholm = Device("Europe/Stockholm", "stockholm");
        var newYork = Device("America/New_York", "new-york");
        Devices(stockholm, newYork);

        var counts = await CreateSut().SendNewEpisodesAsync(User, Since);

        counts.Should().Be(new PushRunCounts(Sent: 1, Held: 1));
        _pushed.Select(p => p.Target.Endpoint).Should().Equal(newYork.Endpoint);
        _subscriptions.Verify(x => x.HoldAsync(stockholm.Id, Since,
            new DateTime(2026, 9, 16, 6, 0, 0, DateTimeKind.Utc), It.IsAny<CancellationToken>()), Times.Once,
            "held until 08:00 the next morning in Stockholm");
    }

    [Test]
    public async Task ADeviceWithAnUnknownZone_Should_KeepQuietHoursInUtc()
    {
        Devices(Device("Not/AZone"));

        var counts = await CreateSut(now: new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Utc)).SendNewEpisodesAsync(User, Since);

        counts.Held.Should().Be(1);
    }

    [Test]
    public async Task ADeviceStillHoldingLastNightsPush_Should_GetOnePushCoveringBoth()
    {
        _user.PushQuietHours = false;
        var heldSince = Evening.AddHours(-12);
        var device = Device("America/New_York", heldSince: heldSince, heldUntil: Evening.AddHours(1));
        Devices(device);

        await CreateSut().SendNewEpisodesAsync(User, Since);

        _notifications.Verify(x => x.GetUnreadNewEpisodesSinceAsync(User, heldSince, It.IsAny<CancellationToken>()), Times.Once);
        _subscriptions.Verify(x => x.MarkDeliveredAsync(device.Id, Evening, It.IsAny<CancellationToken>()), Times.Once,
            "delivery clears the hold");
    }

    [Test]
    public async Task NothingUnread_Should_SendNothing()
    {
        _user.PushQuietHours = false;
        Devices(Device("UTC"));
        Unread();

        var counts = await CreateSut().SendNewEpisodesAsync(User, Since);

        counts.Should().Be(PushRunCounts.None);
        _pushed.Should().BeEmpty();
    }

    [Test]
    public async Task PushOff_Should_SendAndReadNothing()
    {
        _options.Enabled = false;

        (await CreateSut().SendNewEpisodesAsync(User, Since)).Should().Be(PushRunCounts.None);
        (await CreateSut().ReleaseHeldAsync()).Should().Be(PushRunCounts.None);
        (await CreateSut().SendTestAsync(User)).Should().Be(PushRunCounts.None);

        _subscriptions.VerifyNoOtherCalls();
        _sender.VerifyNoOtherCalls();
    }

    // ---- what the push service answers ----------------------------------------------------

    [Test]
    public async Task AGoneSubscription_Should_BeDeleted_AndAFailedOneKept()
    {
        _user.PushQuietHours = false;
        var gone = Device("UTC", "gone");
        var flaky = Device("UTC", "flaky");
        var fine = Device("UTC", "fine");
        Devices(gone, flaky, fine);
        _sender.Setup(x => x.SendAsync(It.Is<PushTarget>(t => t.Endpoint == gone.Endpoint), It.IsAny<PushPayload>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushDelivery.Gone);
        _sender.Setup(x => x.SendAsync(It.Is<PushTarget>(t => t.Endpoint == flaky.Endpoint), It.IsAny<PushPayload>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushDelivery.Failed);

        var counts = await CreateSut().SendNewEpisodesAsync(User, Since);

        counts.Should().Be(new PushRunCounts(Sent: 1, Failed: 2, Removed: 1));
        _subscriptions.Verify(x => x.DeleteAsync(gone.Id, It.IsAny<CancellationToken>()), Times.Once);
        _subscriptions.Verify(x => x.DeleteAsync(flaky.Id, It.IsAny<CancellationToken>()), Times.Never);
        _subscriptions.Verify(x => x.MarkDeliveredAsync(fine.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ASenderThatThrows_Should_CountAsFailed_AndNotStopTheNextDevice()
    {
        _user.PushQuietHours = false;
        var broken = Device("UTC", "broken");
        var fine = Device("UTC", "fine");
        Devices(broken, fine);
        _sender.Setup(x => x.SendAsync(It.Is<PushTarget>(t => t.Endpoint == broken.Endpoint), It.IsAny<PushPayload>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected"));

        var counts = await CreateSut().SendNewEpisodesAsync(User, Since);

        counts.Should().Be(new PushRunCounts(Sent: 1, Failed: 1));
    }

    // ---- releasing held pushes at 08:00 ------------------------------------------------------

    [Test]
    public async Task ADueHold_Should_BeSent_SummarisingWhatIsStillUnreadSinceItBegan()
    {
        var heldSince = Evening;
        var device = Device("Europe/Stockholm", heldSince: heldSince, heldUntil: Evening.AddHours(9));
        var eight = new DateTime(2026, 9, 16, 6, 0, 0, DateTimeKind.Utc);
        _subscriptions.Setup(x => x.GetDueHeldAsync(eight, It.IsAny<CancellationToken>())).ReturnsAsync([device]);
        Unread(Notice("Silo", "S02E05"));

        var counts = await CreateSut(now: eight).ReleaseHeldAsync();

        counts.Should().Be(new PushRunCounts(Sent: 1));
        _pushed.Single().Payload.Title.Should().Be("Silo: S02E05 is out");
        _notifications.Verify(x => x.GetUnreadNewEpisodesSinceAsync(User, heldSince, It.IsAny<CancellationToken>()), Times.Once);
        _subscriptions.Verify(x => x.MarkDeliveredAsync(device.Id, eight, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ADueHold_Should_BeDropped_WhenEveryNotificationInItWasRead()
    {
        var device = Device("Europe/Stockholm", heldSince: Evening, heldUntil: Evening.AddHours(9));
        _subscriptions.Setup(x => x.GetDueHeldAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([device]);
        Unread();

        var counts = await CreateSut(now: Evening.AddHours(9)).ReleaseHeldAsync();

        counts.Should().Be(new PushRunCounts(Dropped: 1));
        _pushed.Should().BeEmpty();
        _subscriptions.Verify(x => x.ClearHoldAsync(device.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AFailedRelease_Should_ClearTheHold_RatherThanRetryAllDay()
    {
        var device = Device("Europe/Stockholm", heldSince: Evening, heldUntil: Evening.AddHours(9));
        _subscriptions.Setup(x => x.GetDueHeldAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([device]);
        _sender.Setup(x => x.SendAsync(It.IsAny<PushTarget>(), It.IsAny<PushPayload>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushDelivery.Failed);

        var counts = await CreateSut(now: Evening.AddHours(9)).ReleaseHeldAsync();

        counts.Should().Be(new PushRunCounts(Failed: 1));
        _subscriptions.Verify(x => x.ClearHoldAsync(device.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- the admin's test push ---------------------------------------------------------------

    [Test]
    public async Task TestPush_Should_GoToEveryDeviceOfTheUser_WhateverTheTime()
    {
        Devices(Device("Europe/Stockholm", "a"), Device("Europe/Stockholm", "b"));

        var counts = await CreateSut().SendTestAsync(User);

        counts.Should().Be(new PushRunCounts(Sent: 2), "it is 23:00 on both, and a test ignores quiet hours");
        _pushed.Should().OnlyContain(p => p.Payload.Title == "Recall test notification");
        _notifications.VerifyNoOtherCalls();
    }
}
