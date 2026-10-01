using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;

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
    public async Task RecordFailedAttemptAsync_Should_KeepTheBodies_ForTheRetry()
    {
        var id = Guid.NewGuid();

        await using var dbContext = new AppDbContext(_dbOptions);
        var sut = new EmailRepository(dbContext);
        await sut.AddAsync(SignInEmail(id));

        await sut.RecordFailedAttemptAsync(id);

        var pending = await sut.GetPendingAsync(maxCount: 10, maxAttempts: 5);
        pending.Should().ContainSingle();
        pending[0].SendAttempts.Should().Be(1);
        pending[0].Body.Should().Contain("raw-secret-token");
    }
}
