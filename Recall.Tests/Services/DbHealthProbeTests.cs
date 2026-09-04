using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AwesomeAssertions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Services.Health;

namespace Recall.Tests.Services;

[TestFixture]
public sealed class DbHealthProbeTests
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

        await using var db = new AppDbContext(_dbOptions);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    [Test]
    public async Task IsDatabaseReachableAsync_ReturnsTrue_WhenTheDatabaseAnswers()
    {
        await using var db = new AppDbContext(_dbOptions);
        var probe = new DbHealthProbe(db, NullLogger<DbHealthProbe>.Instance);

        (await probe.IsDatabaseReachableAsync()).Should().BeTrue();
    }

    [Test]
    public async Task IsDatabaseReachableAsync_ReturnsFalse_WhenTheProbeCannotConnect()
    {
        // A SQLite data source that can't be opened (read-only mode over a path
        // that doesn't exist) — CanConnectAsync must fold that into `false`,
        // not throw.
        var deadOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=/nonexistent-dir/recall-health-probe.db;Mode=ReadOnly")
            .Options;

        await using var db = new AppDbContext(deadOptions);
        var probe = new DbHealthProbe(db, NullLogger<DbHealthProbe>.Instance);

        (await probe.IsDatabaseReachableAsync()).Should().BeFalse();
    }
}
