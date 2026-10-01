using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Tests.Infrastructure.Persistence;

[TestFixture]
public sealed class AuditTimestampTests
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

    [Test]
    public async Task SaveChanges_Should_StampBothTimestamps_OnInsert_WhateverTheCallerSet()
    {
        var id = Guid.NewGuid();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            dbContext.AppUsers.Add(new AppUserEntity
            {
                Id = id,
                Username = "alice",
                Email = "alice@test.local",
                CreatedUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await dbContext.SaveChangesAsync();
        }

        await using var read = new AppDbContext(_dbOptions);
        var user = await read.AppUsers.AsNoTracking().SingleAsync(x => x.Id == id);

        user.CreatedUtc.Should().BeAfter(before);
        user.UpdatedUtc.Should().Be(user.CreatedUtc);
    }

    [Test]
    public async Task SaveChanges_Should_MoveOnlyUpdatedUtc_OnUpdate()
    {
        var id = Guid.NewGuid();

        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            dbContext.AppUsers.Add(new AppUserEntity { Id = id, Username = "alice", Email = "alice@test.local" });
            await dbContext.SaveChangesAsync();
        }

        // Push the row into the past so the update's stamp is distinguishable.
        var created = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var dbContext = new AppDbContext(_dbOptions))
        {
            await dbContext.AppUsers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CreatedUtc, created)
                .SetProperty(x => x.UpdatedUtc, created));

            var user = await dbContext.AppUsers.SingleAsync(x => x.Id == id);
            user.Username = "alice-renamed";
            await dbContext.SaveChangesAsync();
        }

        await using var read = new AppDbContext(_dbOptions);
        var saved = await read.AppUsers.AsNoTracking().SingleAsync(x => x.Id == id);

        saved.CreatedUtc.Should().Be(created);
        saved.UpdatedUtc.Should().BeAfter(created);
    }

    [Test]
    public void EveryEntityWithBothTimestampColumns_Should_OptIn()
    {
        // Adding CreatedUtc + UpdatedUtc to a new entity without implementing the
        // interface would leave both at DateTime.MinValue; this catches that.
        using var dbContext = new AppDbContext(_dbOptions);

        var withBothColumns = dbContext.Model.GetEntityTypes()
            .Where(t => t.FindProperty(nameof(IHasAuditTimestamps.CreatedUtc)) is not null
                        && t.FindProperty(nameof(IHasAuditTimestamps.UpdatedUtc)) is not null)
            .Select(t => t.ClrType)
            .ToList();

        withBothColumns.Should().HaveCount(10);
        withBothColumns.Should().OnlyContain(t => typeof(IHasAuditTimestamps).IsAssignableFrom(t));
    }
}
