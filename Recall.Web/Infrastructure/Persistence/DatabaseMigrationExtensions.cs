using Microsoft.EntityFrameworkCore;

namespace Recall.Web.Infrastructure.Persistence;

public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Applies any pending EF Core migrations before the app starts serving.
    /// Only forward migrations that ship in the assembly are run, so existing
    /// data is preserved — this never drops or recreates the database.
    /// Returns false when the app must not start: in Production, a database
    /// with no applied migrations at all (see <see cref="DatabaseStartup"/>).
    ///
    /// Set <c>Database:MigrateOnStartup=false</c> (config or
    /// <c>Database__MigrateOnStartup</c> env var) to skip it, e.g. if you'd
    /// rather run migrations as a separate deploy step.
    /// </summary>
    public static Task<bool> MigrateDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default) =>
        DatabaseStartup.MigrateAsync(
            app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            DatabaseStartup.Settings.From(app.Configuration, app.Environment),
            app.Services.GetRequiredService<ILogger<AppDbContext>>(),
            retryDelay: TimeSpan.FromSeconds(3),
            cancellationToken);
}

/// <summary>
/// The startup migration, with production's guard against an empty database.
/// </summary>
/// <remarks>
/// <para>
/// The guard: in Production, a database that has no applied migrations
/// (<c>__EFMigrationsHistory</c> missing or empty) stops the app with a
/// Critical line instead of being migrated. Production's database lives on a
/// named Docker volume; a compose file pointing at a new, empty volume (a
/// typo, a renamed volume, a migration step skipped) would otherwise get a
/// fresh schema and a running site with no users, and new sign-ups would then
/// write into it. Stopping makes the deploy's <c>up --wait</c> fail while the
/// real data is still where it was.
/// </para>
/// <para>
/// A genuine first install sets <c>Database:AllowEmptyDatabase=true</c>
/// (<c>Database__AllowEmptyDatabase=true</c> in .env.prod) for its first start
/// and removes it afterwards. Development and tests are not guarded.
/// </para>
/// </remarks>
public static class DatabaseStartup
{
    public const string AllowEmptyDatabaseKey = "Database:AllowEmptyDatabase";
    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    /// <summary>How many times the database is tried while it may still be starting.</summary>
    public const int MaxAttempts = 10;

    /// <summary>What the startup migration was told to do.</summary>
    /// <param name="MigrateOnStartup">False skips migrating, and the guard with it.</param>
    /// <param name="GuardEmptyDatabase">True in Production: an empty database stops the app.</param>
    /// <param name="AllowEmptyDatabase">Lets a guarded app migrate an empty database (a first install).</param>
    public sealed record Settings(bool MigrateOnStartup, bool GuardEmptyDatabase, bool AllowEmptyDatabase)
    {
        public static Settings From(IConfiguration configuration, IHostEnvironment environment) => new(
            MigrateOnStartup: configuration.GetValue(MigrateOnStartupKey, true),
            GuardEmptyDatabase: environment.IsProduction(),
            AllowEmptyDatabase: configuration.GetValue(AllowEmptyDatabaseKey, false));
    }

    /// <summary>
    /// Migrates the database, retrying while it is not reachable yet. Returns
    /// false, after logging a Critical line, when the guard refuses an empty
    /// database; true otherwise. A database that never becomes reachable
    /// throws after <see cref="MaxAttempts"/> tries, as before.
    /// </summary>
    public static async Task<bool> MigrateAsync(
        IDbContextFactory<AppDbContext> factory,
        Settings settings,
        ILogger logger,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (!settings.MigrateOnStartup)
        {
            logger.LogInformation("Startup database migration is disabled (Database:MigrateOnStartup=false).");
            return true;
        }

        // The DB container may still be coming up on a fresh `compose up`, even
        // with a healthcheck gate — retry a few times before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);

                if (settings.GuardEmptyDatabase)
                {
                    // Empty when __EFMigrationsHistory is missing or has no rows.
                    var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
                    if (!applied.Any() && !MayMigrateEmptyDatabase(db, settings, logger))
                        return false;
                }

                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count == 0)
                {
                    logger.LogInformation("Database schema is up to date; no migrations to apply.");
                    return true;
                }

                logger.LogInformation(
                    "Applying {Count} pending migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));

                await db.Database.MigrateAsync(cancellationToken);

                logger.LogInformation("Database migrations applied successfully.");
                return true;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "Database not ready for migration (attempt {Attempt}/{Max}); retrying in {Delay}s.",
                    attempt, MaxAttempts, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private static bool MayMigrateEmptyDatabase(AppDbContext db, Settings settings, ILogger logger)
    {
        var connection = db.Database.GetDbConnection();

        if (settings.AllowEmptyDatabase)
        {
            logger.LogWarning(
                "The database {Database} on {Host} has no applied migrations; creating the schema because " +
                "Database:AllowEmptyDatabase is true. Remove that setting once this first start is done.",
                connection.Database, connection.DataSource);
            return true;
        }

        logger.LogCritical(
            "The database {Database} on {Host} has no applied migrations (__EFMigrationsHistory is missing or empty). " +
            "Not starting: production's data is expected there, and this looks like a new, empty volume. Check the " +
            "postgres service's volume in compose.prod.yml. For a genuine first install, set " +
            "Database__AllowEmptyDatabase=true in .env.prod for the first start, then remove it.",
            connection.Database, connection.DataSource);
        return false;
    }
}
