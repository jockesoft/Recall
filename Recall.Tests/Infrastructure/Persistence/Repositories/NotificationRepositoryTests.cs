using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

/// <summary>What a push reads back: the user's unread new-episode notifications since a moment, with the series name and code.</summary>
[TestFixture]
public sealed class NotificationRepositoryTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private Guid _alice;
    private Guid _bob;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = new AppDbContext(_dbOptions);
        await db.Database.EnsureCreatedAsync();
        _alice = Guid.NewGuid();
        _bob = Guid.NewGuid();
        db.AppUsers.AddRange(
            new AppUserEntity { Id = _alice, Username = "alice", Email = "alice@test.local" },
            new AppUserEntity { Id = _bob, Username = "bob", Email = "bob@test.local" });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    private NotificationRepository NewRepository(AppDbContext db) => new(db, NullLogger<NotificationRepository>.Instance);

    [Test]
    public async Task AddNewEpisodeNotification_Should_StoreTheSeriesNameAndEpisodeCode_ForThePush()
    {
        await using var db = new AppDbContext(_dbOptions);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await NewRepository(db).AddNewEpisodeNotificationAsync(
            _alice, 1, 900, 1, "New episode of Silo", "S02E05 is out now.", "Silo", "S02E05", [900]);

        var notice = (await NewRepository(db).GetUnreadNewEpisodesSinceAsync(_alice, before)).Single();
        notice.SeriesName.Should().Be("Silo");
        notice.EpisodeCode.Should().Be("S02E05");
        notice.EpisodeTvdbId.Should().Be(900);
        notice.EpisodeCount.Should().Be(1);
    }

    [Test]
    public async Task GetUnreadNewEpisodesSince_Should_LeaveOutReadOnes_OlderOnes_AndOtherUsers()
    {
        var since = new DateTime(2026, 10, 27, 22, 0, 0, DateTimeKind.Utc);
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.Notifications.AddRange(
                Row(_alice, "Silo", read: false),
                Row(_alice, "The Bear", read: false),
                Row(_alice, "Read already", read: true),
                Row(_alice, "Before the hold", read: false),
                Row(_bob, "Someone else's", read: false));
            await seed.SaveChangesAsync();

            // CreatedUtc is stamped by SaveChanges; place the rows in time by hand.
            await seed.Notifications.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, since.AddHours(1)));
            await seed.Notifications.Where(x => x.SeriesName == "The Bear")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, since.AddHours(2)));
            await seed.Notifications.Where(x => x.SeriesName == "Before the hold")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedUtc, since.AddMinutes(-1)));
        }

        await using var db = new AppDbContext(_dbOptions);
        var notices = await NewRepository(db).GetUnreadNewEpisodesSinceAsync(_alice, since);

        notices.Select(n => n.SeriesName).Should().Equal("Silo", "The Bear");
    }

    private static NotificationEntity Row(Guid userId, string series, bool read) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.NewEpisode, Title = $"New episode of {series}",
        SeriesName = series, SeriesTvdbId = 1, EpisodeTvdbId = 900, EpisodeCount = 1, IsRead = read
    };
}
