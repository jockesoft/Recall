using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Services.Metrics;

[TestFixture]
public sealed class SiteMetricRepositoryTests
{
    private static readonly DateOnly Day = new(2026, 11, 27);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        await using var db = new AppDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    [Test]
    public async Task AddAsync_Should_CreateRows_ThenAddToThem_AndGetAsyncShouldReturnTheRange()
    {
        await using (var db = new AppDbContext(_options))
        {
            var repository = new SiteMetricRepository(db);
            await repository.AddAsync([new(Day, "signin.link-sent", 2), new(Day.AddDays(-1), "signin.link-sent", 5)]);
            await repository.AddAsync([new(Day, "signin.link-sent", 3), new(Day, "view.crawler", 1)]);
        }

        await using var read = new AppDbContext(_options);
        var repo = new SiteMetricRepository(read);

        (await repo.GetAsync(Day, Day)).Should().BeEquivalentTo(new[]
        {
            new SiteMetricCount(Day, "signin.link-sent", 5),
            new SiteMetricCount(Day, "view.crawler", 1)
        });
        (await repo.GetAsync(Day.AddDays(-1), Day)).Sum(c => c.Count).Should().Be(11);
        (await read.SiteMetrics.CountAsync()).Should().Be(3, "one row per day and key");
    }
}
