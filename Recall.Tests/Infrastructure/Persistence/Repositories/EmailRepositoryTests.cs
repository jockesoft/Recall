using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;

using Recall.Web.Services;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class EmailRepositoryTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;

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
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _connection.DisposeAsync();
    }

    private static OutboundEmail SignInEmail(Guid id) => new()
    {
        Id = id,
        ToAddress = "alice@test.local",
        Subject = "Your Recall sign-in link",
        Body = "Open https://recall.nu/Account/Verify?token=raw-secret-token",
        HtmlBody = "<a href=\"https://recall.nu/Account/Verify?token=raw-secret-token\">Sign in</a>"
    };

    [Test]
    public async Task MarkSentAsync_Should_EraseBothBodies_ButKeepTheDeliveryRecord()
    {
        var id = Guid.NewGuid();
        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            var sut = new EmailRepository(dbContext);
            await sut.AddAsync(SignInEmail(id));

            await sut.MarkSentAsync(id);
        }

        await using var read = new AppDbContext(_dbOptions);
        var row = await read.Emails.AsNoTracking().SingleAsync(x => x.Id == id);

        row.SentUtc.Should().NotBeNull();
        row.Body.Should().BeEmpty();
        row.HtmlBody.Should().BeNull();
        row.ToAddress.Should().Be("alice@test.local");
        row.Subject.Should().Be("Your Recall sign-in link");
    }

    [Test]
    public async Task MarkSentAsync_Should_LeaveOtherMessagesUntouched()
    {
        var sentId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EmailRepository(dbContext);
        await sut.AddAsync(SignInEmail(sentId));
        await sut.AddAsync(SignInEmail(pendingId));

        await sut.MarkSentAsync(sentId);

        var pending = await sut.GetPendingAsync(maxCount: 10, maxAttempts: 5);
        pending.Should().ContainSingle();
        pending[0].Id.Should().Be(pendingId);
        pending[0].Body.Should().Contain("raw-secret-token", "an unsent message still needs its content to be delivered");
        pending[0].HtmlBody.Should().Contain("raw-secret-token");
    }

    [Test]
    public async Task RecordFailedAttemptAsync_Should_KeepTheBodies_WhileRetriesRemain()
    {
        var id = Guid.NewGuid();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EmailRepository(dbContext);
        await sut.AddAsync(SignInEmail(id));

        await sut.RecordFailedAttemptAsync(id, maxAttempts: 3);
        await sut.RecordFailedAttemptAsync(id, maxAttempts: 3);

        var pending = await sut.GetPendingAsync(maxCount: 10, maxAttempts: 3);
        pending.Should().ContainSingle();
        pending[0].SendAttempts.Should().Be(2);
        pending[0].Body.Should().Contain("raw-secret-token");
        pending[0].HtmlBody.Should().Contain("raw-secret-token");
    }

    [Test]
    public async Task RecordFailedAttemptAsync_Should_EraseBothBodies_OnTheAttemptThatGivesUp()
    {
        var abandonedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            var sut = new EmailRepository(dbContext);
            await sut.AddAsync(SignInEmail(abandonedId));
            await sut.AddAsync(SignInEmail(otherId));

            for (var attempt = 0; attempt < 3; attempt++)
                await sut.RecordFailedAttemptAsync(abandonedId, maxAttempts: 3);

            (await sut.GetPendingAsync(maxCount: 10, maxAttempts: 3))
                .Select(x => x.Id).Should().Equal(otherId);
        }

        await using var read = new AppDbContext(_dbOptions);
        var abandoned = await read.Emails.AsNoTracking().SingleAsync(x => x.Id == abandonedId);
        var other = await read.Emails.AsNoTracking().SingleAsync(x => x.Id == otherId);

        abandoned.SendAttempts.Should().Be(3);
        abandoned.SentUtc.Should().BeNull();
        abandoned.Body.Should().BeEmpty();
        abandoned.HtmlBody.Should().BeNull();
        abandoned.ToAddress.Should().Be("alice@test.local");
        other.Body.Should().Contain("raw-secret-token", "only the message that gave up is erased");
    }

    // ---- order of sending -------------------------------------------------------

    [Test]
    public async Task GetPendingAsync_Should_PutASignInLinkAheadOfAHundredDigestsQueuedBeforeIt()
    {
        var signInId = Guid.NewGuid();
        var queuedAt = new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc);

        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            var sut = new EmailRepository(dbContext);

            for (var i = 0; i < 100; i++)
            {
                await sut.AddAsync(new OutboundEmail
                {
                    Id = Guid.NewGuid(),
                    Priority = MailService.DigestPriority,
                    ToAddress = $"reader{i}@test.local",
                    Subject = "Your week on Recall",
                    Body = "digest",
                    CreatedUtc = queuedAt.AddSeconds(i),
                    UpdatedUtc = queuedAt.AddSeconds(i)
                });
            }

            // Queued last, a quarter of an hour after the first digest.
            await sut.AddAsync(new OutboundEmail
            {
                Id = signInId,
                Priority = MailService.NormalPriority,
                ToAddress = "alice@test.local",
                Subject = "Your Recall sign-in link",
                Body = "link",
                CreatedUtc = queuedAt.AddMinutes(15),
                UpdatedUtc = queuedAt.AddMinutes(15)
            });
        }

        await using var read = new AppDbContext(_dbOptions);
        var batch = await new EmailRepository(read).GetPendingAsync(maxCount: 20, maxAttempts: 5);

        batch.Should().HaveCount(20);
        batch[0].Id.Should().Be(signInId, "someone waiting to sign in must not queue behind a hundred weekly digests");
        batch.Skip(1).Should().OnlyContain(e => e.Priority == MailService.DigestPriority);
        batch.Skip(1).Select(e => e.ToAddress).Should().Equal(
            Enumerable.Range(0, 19).Select(i => $"reader{i}@test.local"), "digests keep their own order, oldest first");
    }

    [Test]
    public async Task MarkSentAsync_And_GivingUp_Should_EraseTheUnsubscribeUrl_WithTheBodies()
    {
        var sentId = Guid.NewGuid();
        var abandonedId = Guid.NewGuid();
        OutboundEmail Digest(Guid id) => new()
        {
            Id = id, Priority = MailService.DigestPriority, ToAddress = "reader@test.local", Subject = "Your week on Recall",
            Body = "digest", HtmlBody = "<p>digest</p>", ListUnsubscribeUrl = "https://recall.example/Digest/OneClick?token=secret"
        };

        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            var sut = new EmailRepository(dbContext);
            await sut.AddAsync(Digest(sentId));
            await sut.AddAsync(Digest(abandonedId));

            (await sut.GetPendingAsync(10, 5)).Should().OnlyContain(e => e.ListUnsubscribeUrl != null, "the URL travels with the queued message");

            await sut.MarkSentAsync(sentId);
            await sut.RecordFailedAttemptAsync(abandonedId, maxAttempts: 1);
        }

        await using var read = new AppDbContext(_dbOptions);
        (await read.Emails.AsNoTracking().Select(x => x.ListUnsubscribeUrl).ToListAsync())
            .Should().OnlyContain(url => url == null, "the URL carries a token, so it goes when the message is done");
    }
}
