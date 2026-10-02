using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

/// <summary>
/// What each retention delete selects — and, more importantly, what it leaves
/// alone. Every test uses the same cutoff: 30 days before <see cref="Now"/>.
/// </summary>
[TestFixture]
public sealed class DataRetentionRepositoryTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddDays(-30);
    private static readonly DateTime Old = Cutoff.AddDays(-1);
    private static readonly DateTime Recent = Cutoff.AddDays(1);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private Guid _userId;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var dbContext = new AppDbContext(_dbOptions);
        await dbContext.Database.EnsureCreatedAsync();

        _userId = Guid.NewGuid();
        dbContext.AppUsers.Add(new AppUserEntity { Id = _userId, Username = "alice", Email = "alice@test.local" });
        await dbContext.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task DeleteLoginTokensAsync_Should_RemoveTokensThatExpiredOrWereConsumedBeforeTheCutoff()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.LoginTokens.AddRange(
                Token("expired-long-ago", expires: Old, consumed: null),
                Token("consumed-long-ago", expires: Old.AddMinutes(10), consumed: Old),
                Token("expired-recently", expires: Recent, consumed: null),
                Token("consumed-recently", expires: Recent.AddMinutes(10), consumed: Recent),
                Token("still-valid", expires: Now.AddMinutes(10), consumed: null));
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(_dbOptions);
        var deleted = await new DataRetentionRepository(dbContext).DeleteLoginTokensAsync(Cutoff);

        deleted.Should().Be(2);
        (await dbContext.LoginTokens.Select(x => x.TokenHash).ToListAsync())
            .Should().BeEquivalentTo(["expired-recently", "consumed-recently", "still-valid"]);
    }

    [Test]
    public async Task DeleteFinishedEmailsAsync_Should_RemoveOldSentAndAbandonedEmails_ButNeverOneStillQueued()
    {
        const int maxSendAttempts = 5;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.Emails.AddRange(
                Email("sent-long-ago", sent: Old, attempts: 0),
                Email("sent-recently", sent: Recent, attempts: 0),
                Email("gave-up-long-ago", sent: null, attempts: 5),
                Email("gave-up-recently", sent: null, attempts: 5),
                Email("queued-for-ages-but-still-retrying", sent: null, attempts: 4),
                Email("queued-for-ages-never-tried", sent: null, attempts: 0));
            await seed.SaveChangesAsync();

            // The audit hook stamps "now" on insert; put the last-attempt time where each case needs it.
            await seed.Emails.Where(x => x.Subject != "gave-up-recently")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedUtc, Old).SetProperty(x => x.CreatedUtc, Old));
            await seed.Emails.Where(x => x.Subject == "gave-up-recently")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedUtc, Recent).SetProperty(x => x.CreatedUtc, Old));
        }

        await using var dbContext = new AppDbContext(_dbOptions);
        var deleted = await new DataRetentionRepository(dbContext).DeleteFinishedEmailsAsync(Cutoff, maxSendAttempts);

        deleted.Should().Be(2);
        (await dbContext.Emails.Select(x => x.Subject).ToListAsync())
            .Should().BeEquivalentTo([
                "sent-recently",
                "gave-up-recently",
                "queued-for-ages-but-still-retrying",
                "queued-for-ages-never-tried"
            ]);
    }

    [Test]
    public async Task DeleteReadNotificationsAsync_Should_RemoveNotificationsReadBeforeTheCutoff_ButNeverAnUnreadOne()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.Notifications.AddRange(
                Notification("read-long-ago", isRead: true, readUtc: Old),
                Notification("read-recently", isRead: true, readUtc: Recent),
                Notification("old-but-unread", isRead: false, readUtc: null),
                Notification("read-without-a-timestamp", isRead: true, readUtc: null));
            await seed.SaveChangesAsync();

            await seed.Notifications.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, Old));
        }

        await using var dbContext = new AppDbContext(_dbOptions);
        var deleted = await new DataRetentionRepository(dbContext).DeleteReadNotificationsAsync(Cutoff);

        deleted.Should().Be(2, "one was read long ago, and one is marked read with only its (old) creation time to go by");
        (await dbContext.Notifications.Select(x => x.Title).ToListAsync())
            .Should().BeEquivalentTo(["read-recently", "old-but-unread"]);
    }

    [Test]
    public async Task DeleteNotifiedEpisodesAsync_Should_RemoveLedgerRowsOlderThanTheCutoff()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.NotifiedEpisodes.AddRange(
                new NotifiedEpisodeEntity { Id = Guid.NewGuid(), UserId = _userId, SeriesTvdbId = 1, EpisodeTvdbId = 101, CreatedUtc = Old },
                new NotifiedEpisodeEntity { Id = Guid.NewGuid(), UserId = _userId, SeriesTvdbId = 1, EpisodeTvdbId = 102, CreatedUtc = Recent });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(_dbOptions);
        var deleted = await new DataRetentionRepository(dbContext).DeleteNotifiedEpisodesAsync(Cutoff);

        deleted.Should().Be(1);
        (await dbContext.NotifiedEpisodes.Select(x => x.EpisodeTvdbId).ToListAsync()).Should().Equal(102);
    }

    [Test]
    public async Task DeleteDigestLedgerAsync_Should_RemoveLedgerRowsOlderThanTheCutoff()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.DigestSends.AddRange(
                new DigestSendEntity { Id = Guid.NewGuid(), UserId = _userId, PeriodStart = new DateOnly(2026, 6, 5), Status = DigestSendStatus.Queued, CreatedUtc = Old },
                new DigestSendEntity { Id = Guid.NewGuid(), UserId = _userId, PeriodStart = new DateOnly(2026, 9, 25), Status = DigestSendStatus.Skipped, CreatedUtc = Recent });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(_dbOptions);
        var deleted = await new DataRetentionRepository(dbContext).DeleteDigestLedgerAsync(Cutoff);

        deleted.Should().Be(1);
        (await dbContext.DigestSends.Select(x => x.PeriodStart).ToListAsync()).Should().Equal(new DateOnly(2026, 9, 25));
    }

    [Test]
    public async Task DeleteCompletedImportJobsAsync_Should_RemoveOldCompletedJobsWithTheirRows_ButNeverOneStillProcessing()
    {
        var oldCompleted = Guid.NewGuid();
        var recentCompleted = Guid.NewGuid();
        var oldButStillProcessing = Guid.NewGuid();

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.WatchlistImportJobs.AddRange(
                Job(oldCompleted, WatchlistImportJobStatus.Completed, completed: Old),
                Job(recentCompleted, WatchlistImportJobStatus.Completed, completed: Recent),
                Job(oldButStillProcessing, WatchlistImportJobStatus.Processing, completed: null));
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(_dbOptions);
        var deleted = await new DataRetentionRepository(dbContext).DeleteCompletedImportJobsAsync(Cutoff);

        deleted.Should().Be(1, "the return value counts jobs, not their rows");
        (await dbContext.WatchlistImportJobs.Select(x => x.Id).ToListAsync())
            .Should().BeEquivalentTo([recentCompleted, oldButStillProcessing]);
        (await dbContext.WatchlistImportItems.Select(x => x.JobId).Distinct().ToListAsync())
            .Should().BeEquivalentTo([recentCompleted, oldButStillProcessing], "the deleted job's two rows went with it");
        (await dbContext.WatchlistImportItems.CountAsync()).Should().Be(4);
    }

    [Test]
    public async Task EveryDelete_Should_ReturnZero_OnEmptyTables()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new DataRetentionRepository(dbContext);

        (await sut.DeleteLoginTokensAsync(Cutoff)).Should().Be(0);
        (await sut.DeleteFinishedEmailsAsync(Cutoff, 5)).Should().Be(0);
        (await sut.DeleteReadNotificationsAsync(Cutoff)).Should().Be(0);
        (await sut.DeleteNotifiedEpisodesAsync(Cutoff)).Should().Be(0);
        (await sut.DeleteCompletedImportJobsAsync(Cutoff)).Should().Be(0);
    }

    private LoginTokenEntity Token(string hash, DateTime expires, DateTime? consumed) => new()
    {
        Id = Guid.NewGuid(),
        UserId = _userId,
        TokenHash = hash,
        ExpiresUtc = expires,
        ConsumedUtc = consumed
    };

    private static EmailEntity Email(string subject, DateTime? sent, int attempts) => new()
    {
        Id = Guid.NewGuid(),
        ToAddress = "alice@test.local",
        Subject = subject,
        Body = string.Empty,
        SentUtc = sent,
        SendAttempts = attempts
    };

    private NotificationEntity Notification(string title, bool isRead, DateTime? readUtc) => new()
    {
        Id = Guid.NewGuid(),
        UserId = _userId,
        Type = NotificationType.NewEpisode,
        Title = title,
        IsRead = isRead,
        ReadUtc = readUtc
    };

    private WatchlistImportJobEntity Job(Guid id, WatchlistImportJobStatus status, DateTime? completed) => new()
    {
        Id = id,
        UserId = _userId,
        FileName = "ratings.csv",
        Status = status,
        TotalCount = 2,
        CreatedUtc = Old.AddDays(-1),
        CompletedUtc = completed,
        Items =
        [
            new WatchlistImportItemEntity { Id = Guid.NewGuid(), RowNumber = 1, ImdbId = "tt1", Title = "One", TitleType = "Movie", CreatedUtc = Old },
            new WatchlistImportItemEntity { Id = Guid.NewGuid(), RowNumber = 2, ImdbId = "tt2", Title = "Two", TitleType = "Movie", CreatedUtc = Old }
        ]
    };
}
