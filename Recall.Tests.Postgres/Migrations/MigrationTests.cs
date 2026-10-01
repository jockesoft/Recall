using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Recall.Tests.Postgres.Migrations;

/// <summary>
/// The other test projects build their schema from the model
/// (<c>EnsureCreated</c>), so nothing else ever executes the migrations.
/// </summary>
[TestFixture]
public sealed class MigrationTests
{
    [Test]
    public async Task AllMigrations_Should_ApplyToAnEmptyDatabase()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var db = database.DbContext;

        await database.MigrateToLatestAsync();

        (await db.Database.GetAppliedMigrationsAsync()).Should().Equal(db.Database.GetMigrations());
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task Model_Should_HaveNoChangesMissingFromTheMigrations()
    {
        await using var database = await MigrationDatabase.CreateAsync();

        // False means an entity or configuration was changed without running
        // `dotnet ef migrations add`.
        database.DbContext.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Test]
    public async Task Migrations_Should_BeANoOp_WhenAppliedTwice()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToLatestAsync();

        // What every app start after the first one does.
        var act = database.MigrateToLatestAsync;

        await act.Should().NotThrowAsync();
    }
}
