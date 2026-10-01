using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Recall.Web.Infrastructure.Mail;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Retention;
using Recall.Web.Infrastructure.Timers;

namespace Recall.Tests.Infrastructure.Timers;

[TestFixture]
public class PruneOldDataTimerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private Mock<IDataRetentionRepository> _repository = null!;

    [SetUp]
    public void SetUp() => _repository = new Mock<IDataRetentionRepository>();

    private PruneOldDataTimer CreateSut(RetentionOptions? retention = null) => new(
        _repository.Object,
        Options.Create(retention ?? new RetentionOptions()),
        Options.Create(new MailOptions { MaxSendAttempts = 5 }),
        new FixedTimeProvider(Now),
        NullLogger<PruneOldDataTimer>.Instance);

    private static DateTime DaysAgo(int days) => Now.UtcDateTime.AddDays(-days);

    [Test]
    public async Task Execute_Should_PruneEveryCategory_WithItsDefaultRetention()
    {
        await CreateSut().Execute(Mock.Of<IJobExecutionContext>());

        _repository.Verify(x => x.DeleteLoginTokensAsync(DaysAgo(7), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteFinishedEmailsAsync(DaysAgo(30), 5, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteReadNotificationsAsync(DaysAgo(90), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteNotifiedEpisodesAsync(DaysAgo(30), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteCompletedImportJobsAsync(DaysAgo(90), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Execute_Should_UseTheConfiguredRetention()
    {
        var retention = new RetentionOptions
        {
            LoginTokenDays = 1,
            EmailDays = 2,
            ReadNotificationDays = 3,
            NotifiedEpisodeDays = 4,
            ImportJobDays = 5
        };

        await CreateSut(retention).Execute(Mock.Of<IJobExecutionContext>());

        _repository.Verify(x => x.DeleteLoginTokensAsync(DaysAgo(1), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteFinishedEmailsAsync(DaysAgo(2), 5, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteReadNotificationsAsync(DaysAgo(3), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteNotifiedEpisodesAsync(DaysAgo(4), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteCompletedImportJobsAsync(DaysAgo(5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Execute_Should_SkipACategory_WhoseRetentionIsZeroOrNegative()
    {
        var retention = new RetentionOptions { EmailDays = 0, ReadNotificationDays = -1 };

        await CreateSut(retention).Execute(Mock.Of<IJobExecutionContext>());

        _repository.Verify(
            x => x.DeleteFinishedEmailsAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(
            x => x.DeleteReadNotificationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(x => x.DeleteLoginTokensAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteNotifiedEpisodesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteCompletedImportJobsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Execute_Should_CarryOn_WhenOneCategoryFails()
    {
        _repository
            .Setup(x => x.DeleteLoginTokensAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("lock timeout"));
        _repository
            .Setup(x => x.DeleteReadNotificationsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("lock timeout"));

        await CreateSut().Execute(Mock.Of<IJobExecutionContext>());

        _repository.Verify(x => x.DeleteFinishedEmailsAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteNotifiedEpisodesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(x => x.DeleteCompletedImportJobsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
