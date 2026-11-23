using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Recall.Web.Infrastructure.Persistence;

namespace Recall.Tests.Postgres.Migrations;

/// <summary>
/// Production's startup guard (<see cref="DatabaseStartup"/>): a database with
/// no applied migrations stops the app instead of being given a fresh schema,
/// unless Database:AllowEmptyDatabase says this is a first install.
/// </summary>
[TestFixture]
public sealed class EmptyDatabaseGuardTests
{
    private static readonly DatabaseStartup.Settings Production =
        new(MigrateOnStartup: true, GuardEmptyDatabase: true, AllowEmptyDatabase: false);

    [Test]
    public async Task Production_Should_RefuseAnEmptyDatabase_AndLeaveItEmpty()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var logger = new Recorder();

        var mayStart = await MigrateAsync(database, Production, logger);

        mayStart.Should().BeFalse();
        var critical = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical).Subject;
        critical.Message.Should().Contain("no applied migrations").And.Contain("Not starting")
            .And.Contain("Database__AllowEmptyDatabase=true");
        (await TableExistsAsync(database, "__EFMigrationsHistory")).Should().BeFalse("nothing is created");
        (await TableExistsAsync(database, "app_user")).Should().BeFalse();
    }

    [Test]
    public async Task Production_Should_RefuseADatabaseWhoseHistoryTableIsEmpty()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.ExecuteAsync(
            """CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);""");
        var logger = new Recorder();

        (await MigrateAsync(database, Production, logger)).Should().BeFalse();

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Critical);
        (await TableExistsAsync(database, "app_user")).Should().BeFalse();
    }

    [Test]
    public async Task Production_Should_MigrateAnEmptyDatabase_WhenAFirstInstallIsAllowed()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var logger = new Recorder();

        var mayStart = await MigrateAsync(database, Production with { AllowEmptyDatabase = true }, logger);

        mayStart.Should().BeTrue();
        (await database.DbContext.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("AllowEmptyDatabase"));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Critical);
    }

    [Test]
    public async Task Production_Should_ApplyPendingMigrations_ToADatabaseThatHasSome()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var migrations = database.DbContext.Database.GetMigrations().ToList();
        await database.MigrateToAsync(migrations[^2]);
        var logger = new Recorder();

        var mayStart = await MigrateAsync(database, Production, logger);

        mayStart.Should().BeTrue();
        (await database.DbContext.Database.GetAppliedMigrationsAsync()).Should().Equal(migrations);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task Production_Should_StartOnAnUpToDateDatabase()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        await database.MigrateToLatestAsync();
        var logger = new Recorder();

        (await MigrateAsync(database, Production, logger)).Should().BeTrue();

        logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("no migrations to apply");
    }

    [Test]
    public async Task OutsideProduction_Should_MigrateAnEmptyDatabase()
    {
        await using var database = await MigrationDatabase.CreateAsync();
        var development = Production with { GuardEmptyDatabase = false };

        (await MigrateAsync(database, development, new Recorder())).Should().BeTrue();

        (await database.DbContext.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task MigrateOnStartupOff_Should_TouchNothing()
    {
        await using var database = await MigrationDatabase.CreateAsync();

        (await MigrateAsync(database, Production with { MigrateOnStartup = false }, new Recorder())).Should().BeTrue();

        (await TableExistsAsync(database, "__EFMigrationsHistory")).Should().BeFalse();
    }

    private static Task<bool> MigrateAsync(MigrationDatabase database, DatabaseStartup.Settings settings, ILogger logger) =>
        DatabaseStartup.MigrateAsync(
            new Factory(database.DbContext.Database.GetDbConnection().Database), settings, logger, retryDelay: TimeSpan.Zero);

    private static async Task<bool> TableExistsAsync(MigrationDatabase database, string table) =>
        await database.ScalarAsync<bool>("SELECT to_regclass(format('%I', $1::text)) IS NOT NULL;", table);

    /// <summary>A fresh unpooled context per call, as the app's factory gives.</summary>
    private sealed class Factory(string databaseName) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => PostgresSuite.NewContext(databaseName, pooling: false);
    }

    private sealed class Recorder : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
