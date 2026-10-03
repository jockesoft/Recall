using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Timers;
using Recall.Web.Services;
using Recall.Web.Services.Import;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.Notifications.Models;

namespace Recall.Tests.Infrastructure.Timers;

[TestFixture]
public class QueueAndNotificationTimerTests
{
    // ---- MailTimer ---------------------------------------------------------------

    [Test]
    public async Task MailTimer_Should_SendOneBatchPerRun()
    {
        var mail = new Mock<IMailService>();

        await new MailTimer(mail.Object, NullLogger<MailTimer>.Instance).Execute(Mock.Of<IJobExecutionContext>());

        mail.Verify(x => x.SendPendingEmailsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MailTimer_Should_SwallowAFailure_SoTheSchedulerKeepsRunningIt()
    {
        var mail = new Mock<IMailService>();
        mail.Setup(x => x.SendPendingEmailsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP down"));
        var sut = new MailTimer(mail.Object, NullLogger<MailTimer>.Instance);

        var act = async () => await sut.Execute(Mock.Of<IJobExecutionContext>());

        await act.Should().NotThrowAsync();
    }

    // ---- WatchlistImportTimer ----------------------------------------------------

    [Test]
    public async Task WatchlistImportTimer_Should_ProcessAtMostFifteenRowsPerRun()
    {
        var import = new Mock<IWatchlistImportService>();

        await new WatchlistImportTimer(import.Object, NullLogger<WatchlistImportTimer>.Instance).Execute(Mock.Of<IJobExecutionContext>());

        import.Verify(x => x.ProcessNextBatchAsync(15, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task WatchlistImportTimer_Should_SwallowAFailure()
    {
        var import = new Mock<IWatchlistImportService>();
        import.Setup(x => x.ProcessNextBatchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var sut = new WatchlistImportTimer(import.Object, NullLogger<WatchlistImportTimer>.Instance);

        var act = async () => await sut.Execute(Mock.Of<IJobExecutionContext>());

        await act.Should().NotThrowAsync();
    }

    // ---- NewEpisodeNotificationTimer -----------------------------------------------

    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<INotificationService> _notifications = null!;
    private List<(Guid UserId, NewEpisodesDigest Digest)> _sent = null!;

    [SetUp]
    public void SetUp()
    {
        _tracked = new Mock<ITrackedSeriesRepository>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _tvDb = new Mock<ITheTvDbService>();
        _notifications = new Mock<INotificationService>();
        _sent = [];

        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int>());
        _notifications
            .Setup(x => x.NotifyNewEpisodesAsync(It.IsAny<Guid>(), It.IsAny<NewEpisodesDigest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, NewEpisodesDigest, CancellationToken>((userId, digest, _) => _sent.Add((userId, digest)))
            .ReturnsAsync(true);
    }

    private NewEpisodeNotificationTimer NotificationJob(DateTimeOffset? now = null) => new(
        _tracked.Object,
        _watches.Object,
        _tvDb.Object,
        _notifications.Object,
        new FixedTimeProvider(now ?? new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
        NullLogger<NewEpisodeNotificationTimer>.Instance);

    private void Tracked(int seriesId, params Guid[] users)
    {
        _tracked
            .Setup(x => x.GetUserIdsTrackingAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);
    }

    private void TrackedSeriesIds(params int[] ids) =>
        _tracked
            .Setup(x => x.GetDistinctTrackedTvdbIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    // A US network series at 20:00 Eastern, like Gold Rush: an episode aired on
    // date D is released at 00:00 UTC on D+1 (EDT). From the caches only.
    private void Series(int id, params EpisodeSummary[] episodes) =>
        _tvDb
            .Setup(x => x.GetCachedSeriesAggregateAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = id, Name = $"Series {id}", AirsTime = "20:00", OriginalCountry = "usa", Episodes = episodes
            });

    private static EpisodeSummary Ep(int id, int number, DateOnly? aired, bool isMovie = false) =>
        new() { Id = id, SeasonNumber = 1, EpisodeNumber = number, Name = $"E{number}", Aired = aired, IsMovie = isMovie };

    [Test]
    public async Task NotificationJob_Should_NotifyEachTrackingUser_AboutEpisodesReleasedInTheLastThreeDays()
    {
        // Now: Oct 1 12:00 UTC. Released within the three days before it: after Sep 28 12:00 UTC.
        TrackedSeriesIds(1);
        Tracked(1, Alice, Bob);
        Series(1,
            Ep(10, 1, Today.AddDays(-4)),            // released Sep 28 00:00 UTC: too old
            Ep(11, 2, Today.AddDays(-3)),            // released Sep 29 00:00 UTC
            Ep(12, 3, Today.AddDays(-1)),            // released Oct 1 00:00 UTC
            Ep(13, 4, Today),                        // airs tonight in the US: released Oct 2 00:00 UTC, not yet
            Ep(14, 5, aired: null),                  // no date
            Ep(15, 6, Today.AddDays(-1), isMovie: true)); // movie-flagged entry

        await NotificationJob().Execute(Mock.Of<IJobExecutionContext>());

        _sent.Select(s => s.UserId).Should().BeEquivalentTo([Alice, Bob]);
        foreach (var (_, digest) in _sent)
        {
            digest.SeriesTvdbId.Should().Be(1);
            digest.SeriesName.Should().Be("Series 1");
            digest.Episodes.Select(e => e.EpisodeTvdbId).Should().Equal(11, 12);
        }
    }

    // ---- Gold Rush S17E01: aired 2026-10-02 at 20:00 Eastern, released 2026-10-03 00:00 UTC ----

    private void GoldRush()
    {
        TrackedSeriesIds(208111);
        Tracked(208111, Alice);
        _tvDb
            .Setup(x => x.GetCachedSeriesAggregateAsync(208111, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = 208111, Name = "Gold Rush", AirsTime = "20:00", OriginalCountry = "usa",
                Episodes = [new EpisodeSummary { Id = 11961330, SeasonNumber = 17, EpisodeNumber = 1, Name = "The Most Gold Wins", Aired = new DateOnly(2026, 10, 2) }]
            });
    }

    [TestCase(2, 0, 30, Description = "the run that used to notify a day early")]
    [TestCase(2, 18, 0, Description = "afternoon in the US")]
    [TestCase(2, 23, 59, Description = "a minute before")]
    public async Task GoldRushS17E01_Should_NotBeNotified_BeforeItAirsInTheUs(int day, int hour, int minute)
    {
        GoldRush();

        await NotificationJob(new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.Zero)).Execute(Mock.Of<IJobExecutionContext>());

        _sent.Should().BeEmpty();
    }

    [Test]
    public async Task GoldRushS17E01_Should_BeNotified_ByTheFirstRunAfterMidnightUtcOnOctoberThird()
    {
        GoldRush();

        await NotificationJob(new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero)).Execute(Mock.Of<IJobExecutionContext>());
        await NotificationJob(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)).Execute(Mock.Of<IJobExecutionContext>());

        var (userId, digest) = _sent.Should().ContainSingle("the hourly run just after the release notifies; the one before does not").Subject;
        userId.Should().Be(Alice);
        digest.Episodes.Select(e => e.EpisodeTvdbId).Should().Equal(11961330);
    }

    [Test]
    public async Task NotificationJob_Should_ReadOnlyCachedSeries_AndNeverAskTheTvDb()
    {
        GoldRush();

        await NotificationJob(new DateTimeOffset(2026, 10, 3, 1, 0, 0, TimeSpan.Zero)).Execute(Mock.Of<IJobExecutionContext>());

        _tvDb.Verify(x => x.GetCachedSeriesAggregateAsync(208111, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task NotificationJob_Should_LeaveOutEpisodesAUserHasAlreadyWatched()
    {
        TrackedSeriesIds(1);
        Tracked(1, Alice, Bob);
        Series(1, Ep(11, 2, Today.AddDays(-2)), Ep(12, 3, Today.AddDays(-1)));
        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(Alice, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int> { 11, 12 });
        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(Bob, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int> { 11 });

        await NotificationJob().Execute(Mock.Of<IJobExecutionContext>());

        var only = _sent.Should().ContainSingle().Subject;
        only.UserId.Should().Be(Bob, "Alice has watched both, so she gets nothing");
        only.Digest.Episodes.Select(e => e.EpisodeTvdbId).Should().Equal(12);
    }

    [Test]
    public async Task NotificationJob_Should_CarryOn_WhenOneSeriesFails()
    {
        TrackedSeriesIds(1, 2, 3);
        Tracked(1, Alice);
        Tracked(3, Alice);
        Series(1, Ep(11, 1, Today.AddDays(-1)));
        Series(3, Ep(31, 1, Today.AddDays(-1)));
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis is away"));

        await NotificationJob().Execute(Mock.Of<IJobExecutionContext>());

        _sent.Select(s => s.Digest.SeriesTvdbId).Should().BeEquivalentTo([1, 3]);
    }

    [Test]
    public async Task NotificationJob_Should_CheckAtMostFiveHundredSeriesPerRun()
    {
        TrackedSeriesIds(Enumerable.Range(1, 520).ToArray());
        _tvDb
            .Setup(x => x.GetCachedSeriesAggregateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesAggregate?)null);

        await NotificationJob().Execute(Mock.Of<IJobExecutionContext>());

        _tvDb.Verify(x => x.GetCachedSeriesAggregateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(500));
    }

    [Test]
    public async Task NotificationJob_Should_DoNothing_WhenNobodyTracksAnything_OrNothingAiredRecently()
    {
        TrackedSeriesIds();
        await NotificationJob().Execute(Mock.Of<IJobExecutionContext>());
        _tvDb.VerifyNoOtherCalls();

        TrackedSeriesIds(1);
        Tracked(1, Alice);
        Series(1, Ep(10, 1, Today.AddDays(-30)));
        await NotificationJob().Execute(Mock.Of<IJobExecutionContext>());

        _sent.Should().BeEmpty();
        _tracked.Verify(x => x.GetUserIdsTrackingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "there is no need to look up who tracks a series that has nothing new");
    }
}
