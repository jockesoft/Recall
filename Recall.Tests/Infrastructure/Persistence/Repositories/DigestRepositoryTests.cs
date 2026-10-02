using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class DigestRepositoryTests
{
    private static readonly DateOnly ThisWeek = new(2026, 10, 2);
    private static readonly DateOnly LastWeek = new(2026, 9, 25);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var dbContext = new AppDbContext(_dbOptions);
        await dbContext.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    private async Task<Guid> SeedUserAsync(string name, DateTime? optedInUtc)
    {
        var id = Guid.NewGuid();
        await using var dbContext = new AppDbContext(_dbOptions);
        dbContext.AppUsers.Add(new AppUserEntity { Id = id, Username = name, Email = $"{name}@test.local", DigestOptedInUtc = optedInUtc });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private static DateTime OptIn(int day) => new(2026, 9, day, 12, 0, 0, DateTimeKind.Utc);

    private static OutboundEmail Digest(string to) => new()
    {
        Id = Guid.NewGuid(), Priority = MailService.DigestPriority, ToAddress = to, Subject = "Your week on Recall",
        Body = "text", HtmlBody = "<p>html</p>", ListUnsubscribeUrl = "https://recall.example/Digest/OneClick?token=t"
    };

    [Test]
    public async Task DueRecipients_Should_BeThoseWhoOptedIn_AndHaveNoLedgerRowForTheWeek()
    {
        var second = await SeedUserAsync("second", OptIn(10));
        var first = await SeedUserAsync("first", OptIn(1));
        var done = await SeedUserAsync("done", OptIn(5));
        var doneLastWeekOnly = await SeedUserAsync("last-week", OptIn(20));
        await SeedUserAsync("never-opted-in", null);

        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            var sut = new DigestRepository(dbContext);
            (await sut.RecordAsync(done, ThisWeek, DigestSendStatus.Skipped, null)).Should().BeTrue();
            (await sut.RecordAsync(doneLastWeekOnly, LastWeek, DigestSendStatus.Queued, Digest("last-week@test.local"))).Should().BeTrue();
        }

        await using var read = new AppDbContext(_dbOptions);
        var due = await new DigestRepository(read).GetDueRecipientsAsync(ThisWeek, 10);

        due.Select(r => r.UserId).Should().Equal(
            [first, second, doneLastWeekOnly], "earliest opt-in first; a ledger row for another week does not count");
        due[0].Should().Be(new DigestRecipient(first, "first@test.local", "first"));
    }

    [Test]
    public async Task DueRecipients_Should_RespectTheCap()
    {
        for (var i = 1; i <= 5; i++)
            await SeedUserAsync($"user{i}", OptIn(i));

        await using var read = new AppDbContext(_dbOptions);
        var sut = new DigestRepository(read);

        (await sut.GetDueRecipientsAsync(ThisWeek, 2)).Select(r => r.Username).Should().Equal("user1", "user2");
        (await sut.GetDueRecipientsAsync(ThisWeek, 0)).Should().BeEmpty();
    }

    [Test]
    public async Task Record_Should_WriteTheLedgerRowAndTheEmailTogether()
    {
        var user = await SeedUserAsync("saga", OptIn(1));

        await using (var dbContext = new AppDbContext(_dbOptions))
            (await new DigestRepository(dbContext).RecordAsync(user, ThisWeek, DigestSendStatus.Queued, Digest("saga@test.local"))).Should().BeTrue();

        await using var read = new AppDbContext(_dbOptions);
        var ledger = await read.DigestSends.AsNoTracking().SingleAsync();
        ledger.UserId.Should().Be(user);
        ledger.PeriodStart.Should().Be(ThisWeek);
        ledger.Status.Should().Be(DigestSendStatus.Queued);

        var email = await read.Emails.AsNoTracking().SingleAsync();
        email.ToAddress.Should().Be("saga@test.local");
        email.Priority.Should().Be(MailService.DigestPriority);
        email.ListUnsubscribeUrl.Should().Be("https://recall.example/Digest/OneClick?token=t");
        email.SentUtc.Should().BeNull("it is queued for the mail job");
    }

    [Test]
    public async Task Record_Should_RefuseASecondDigestForTheSameWeek_AndQueueNothing()
    {
        var user = await SeedUserAsync("saga", OptIn(1));

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new DigestRepository(dbContext);

        (await sut.RecordAsync(user, ThisWeek, DigestSendStatus.Queued, Digest("saga@test.local"))).Should().BeTrue();
        (await sut.RecordAsync(user, ThisWeek, DigestSendStatus.Queued, Digest("saga@test.local"))).Should().BeFalse();
        (await sut.RecordAsync(user, ThisWeek, DigestSendStatus.Skipped, null)).Should().BeFalse();

        (await dbContext.DigestSends.CountAsync()).Should().Be(1);
        (await dbContext.Emails.CountAsync()).Should().Be(1, "the second attempt must not queue another email");
    }

    [Test]
    public async Task Record_Should_AllowTheSameUserAgain_NextWeek()
    {
        var user = await SeedUserAsync("saga", OptIn(1));

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new DigestRepository(dbContext);

        (await sut.RecordAsync(user, LastWeek, DigestSendStatus.Queued, Digest("saga@test.local"))).Should().BeTrue();
        (await sut.RecordAsync(user, ThisWeek, DigestSendStatus.Queued, Digest("saga@test.local"))).Should().BeTrue();
    }
}
