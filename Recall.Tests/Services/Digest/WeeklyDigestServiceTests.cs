using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.Internal;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Timers;
using Recall.Web.Services;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Services.Digest;

[TestFixture]
public sealed class WeeklyDigestServiceTests
{
    // Friday 2026-10-02, 16:00 UTC: an hour after the default send time.
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Period = new(2026, 10, 2);

    private Mock<IDigestRepository> _repository = null!;
    private Mock<IDigestComposer> _composer = null!;
    private DigestOptions _options = null!;
    private SiteOptions _site = null!;
    private FixedTimeProvider _clock = null!;
    private List<(Guid UserId, DigestSendStatus Status, OutboundEmail? Email)> _recorded = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IDigestRepository>();
        _composer = new Mock<IDigestComposer>();
        _options = new DigestOptions { Enabled = true, MaxPerRun = 50 };
        _site = new SiteOptions { BaseUrl = "https://recall.example/" };
        _clock = new FixedTimeProvider(Now);
        _recorded = [];

        _repository
            .Setup(x => x.RecordAsync(It.IsAny<Guid>(), Period, It.IsAny<DigestSendStatus>(), It.IsAny<OutboundEmail?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DateOnly, DigestSendStatus, OutboundEmail?, CancellationToken>((user, _, status, email, _) => _recorded.Add((user, status, email)))
            .ReturnsAsync(true);
    }

    private WeeklyDigestService CreateSut() =>
        new(_repository.Object, _composer.Object, Options.Create(_options), Options.Create(_site), _clock, NullLogger<WeeklyDigestService>.Instance);

    private static DigestRecipient Recipient(string name) => new(Guid.NewGuid(), $"{name}@test.local", name);

    private void Due(params DigestRecipient[] recipients) =>
        _repository.Setup(x => x.GetDueRecipientsAsync(Period, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(recipients);

    private static readonly DigestContent SomethingToSay = new(
        DigestSection<DigestPremiere>.Empty,
        new DigestSection<DigestEpisodeLine>([new DigestEpisodeLine(1, "Show", 1, 2, 2, 1, 11, null, Period)], 0),
        DigestSection<DigestEpisodeLine>.Empty);

    private static readonly DigestContent NothingToSay = new(
        DigestSection<DigestPremiere>.Empty, DigestSection<DigestEpisodeLine>.Empty, DigestSection<DigestEpisodeLine>.Empty);

    private void Composes(DigestRecipient recipient, bool somethingToSay) =>
        _composer
            .Setup(x => x.ComposeAsync(recipient.UserId, recipient.Username, Now.UtcDateTime, "https://recall.example", It.IsAny<IDictionary<int, SeriesAggregate?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(somethingToSay
                ? new ComposedDigest(SomethingToSay, new DigestEmail("Your week on Recall: 1 to watch", "text", "<p>html</p>"), $"https://recall.example/Digest/OneClick?token={recipient.Username}")
                : new ComposedDigest(NothingToSay, null, "unused"));

    [Test]
    public async Task Run_Should_QueueADigest_WithItsLedgerRow_AtDigestPriority_AndWithTheUnsubscribeHeaderAddress()
    {
        var saga = Recipient("saga");
        Due(saga);
        Composes(saga, somethingToSay: true);

        var result = await CreateSut().RunAsync();

        result.Should().Be(new DigestRunResult(Period, Queued: 1, Skipped: 0, Failed: 0));
        var (userId, status, email) = _recorded.Should().ContainSingle().Subject;
        userId.Should().Be(saga.UserId);
        status.Should().Be(DigestSendStatus.Queued);
        email!.ToAddress.Should().Be("saga@test.local");
        email.Subject.Should().Be("Your week on Recall: 1 to watch");
        email.Body.Should().Be("text");
        email.HtmlBody.Should().Be("<p>html</p>");
        email.Priority.Should().Be(MailService.DigestPriority, "sign-in links go out before digests");
        email.ListUnsubscribeUrl.Should().Be("https://recall.example/Digest/OneClick?token=saga");
    }

    [Test]
    public async Task Run_Should_SendNothing_ButRecordTheWeek_WhenThereIsNothingToSay()
    {
        var quiet = Recipient("quiet");
        Due(quiet);
        Composes(quiet, somethingToSay: false);

        var result = await CreateSut().RunAsync();

        result.Should().Be(new DigestRunResult(Period, Queued: 0, Skipped: 1, Failed: 0));
        _recorded.Should().Equal([(quiet.UserId, DigestSendStatus.Skipped, (OutboundEmail?)null)]);
    }

    [Test]
    public async Task Run_Should_AskForAtMostMaxPerRunRecipients()
    {
        _options.MaxPerRun = 7;
        Due();

        await CreateSut().RunAsync();

        _repository.Verify(x => x.GetDueRecipientsAsync(Period, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Run_Should_CarryOn_WhenOneUsersDigestFails_AndLeaveThatUserForTheNextRun()
    {
        var (first, broken, last) = (Recipient("first"), Recipient("broken"), Recipient("last"));
        Due(first, broken, last);
        Composes(first, somethingToSay: true);
        Composes(last, somethingToSay: true);
        _composer
            .Setup(x => x.ComposeAsync(broken.UserId, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IDictionary<int, SeriesAggregate?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await CreateSut().RunAsync();

        result.Should().Be(new DigestRunResult(Period, Queued: 2, Skipped: 0, Failed: 1));
        _recorded.Select(r => r.UserId).Should().Equal([first.UserId, last.UserId], "no ledger row for the failed user, so the next hourly run tries again");
    }

    [Test]
    public async Task Run_Should_NotCountADigest_ThatAnotherRunRecordedFirst()
    {
        var saga = Recipient("saga");
        Due(saga);
        Composes(saga, somethingToSay: true);
        _repository
            .Setup(x => x.RecordAsync(saga.UserId, Period, DigestSendStatus.Queued, It.IsAny<OutboundEmail?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        (await CreateSut().RunAsync()).Queued.Should().Be(0);
    }

    [Test]
    public async Task Run_Should_DoNothing_WhenTheDigestIsDisabled()
    {
        _options.Enabled = false;

        (await CreateSut().RunAsync()).Should().Be(DigestRunResult.NothingDue);
        _repository.VerifyNoOtherCalls();
        _composer.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Run_Should_DoNothing_OutsideTheSendWindow()
    {
        _clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));   // a Wednesday

        (await CreateSut().RunAsync()).Should().Be(DigestRunResult.NothingDue);
        _repository.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Run_Should_SendNothing_WithoutABaseUrl()
    {
        _site.BaseUrl = null;

        (await CreateSut().RunAsync()).Should().Be(DigestRunResult.NothingDue, "links that lead nowhere must not be mailed out");
        _repository.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TheJob_Should_RunTheService_AndSwallowAFailure()
    {
        var service = new Mock<IWeeklyDigestService>();
        service.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var job = new WeeklyDigestTimer(service.Object, NullLogger<WeeklyDigestTimer>.Instance);

        var act = async () => await job.Execute(Mock.Of<IJobExecutionContext>());

        await act.Should().NotThrowAsync("an exception must not escape into the scheduler");
        service.Verify(x => x.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
