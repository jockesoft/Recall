using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Recall.Tests.Postgres;

/// <summary>
/// Starts one PostgreSQL container for the whole test run and builds a template
/// database in it by applying the real EF Core migrations. Every fixture then
/// gets its own database cloned from that template (see <see cref="PostgresFixture"/>),
/// which takes milliseconds instead of re-running the migrations.
///
/// Without Docker the suite is reported as ignored, so <c>dotnet test Recall.sln</c>
/// stays green on a machine that has none. When the <c>CI</c> environment
/// variable is set (GitHub Actions sets it) the same situation fails instead: an
/// ignored suite there would go unnoticed.
/// </summary>
[SetUpFixture]
public sealed class PostgresSuite
{
    /// <summary>Same image as production (<c>compose.prod.yml</c>) and local development.</summary>
    public const string Image = "postgres:18.1";

    private const string TemplateDatabase = "recall_template";

    private static PostgreSqlContainer? _container;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (DockerAvailability.IsUnreachable(ex))
        {
            _container = null;
            if (DockerAvailability.IsCi)
                throw new InvalidOperationException(DockerAvailability.Describe(ex, isCi: true), ex);

            Assert.Ignore(DockerAvailability.Describe(ex, isCi: false));
        }

        var containerStarted = stopwatch.Elapsed;

        await ExecuteOnServerAsync($"""CREATE DATABASE "{TemplateDatabase}";""");
        await using (var db = NewContext(TemplateDatabase, pooling: false))
        {
            await db.Database.MigrateAsync();
        }

        // Shown in the test output so the suite's fixed cost stays visible.
        await TestContext.Progress.WriteLineAsync(
            FormattableString.Invariant($"[PostgresSuite] container ready in {containerStarted.TotalSeconds:F1} s, ") +
            FormattableString.Invariant($"migrations applied in {(stopwatch.Elapsed - containerStarted).TotalSeconds:F1} s."));
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Creates a database that already has the current schema, by cloning the template.</summary>
    public static async Task<string> CreateMigratedDatabaseAsync()
    {
        var name = NewDatabaseName();
        await ExecuteOnServerAsync($"""CREATE DATABASE "{name}" TEMPLATE "{TemplateDatabase}";""");
        return name;
    }

    /// <summary>Creates a database with nothing in it, for tests that apply migrations themselves.</summary>
    public static async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = NewDatabaseName();
        await ExecuteOnServerAsync($"""CREATE DATABASE "{name}";""");
        return name;
    }

    public static string ConnectionString(string database, bool pooling = true)
    {
        var container = _container
            ?? throw new InvalidOperationException("The PostgreSQL container is not running.");

        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
            // Fail with the real error instead of hiding it behind a parameter placeholder.
            IncludeErrorDetail = true
        }.ConnectionString;
    }

    /// <summary>
    /// A context on <paramref name="database"/> without connection pooling: no
    /// idle connection is left behind, which <c>CREATE DATABASE … TEMPLATE</c>
    /// would refuse to clone past.
    /// </summary>
    public static AppDbContext NewContext(string database, bool pooling)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString(database, pooling))
            .Options;

        return new AppDbContext(options);
    }

    private static string NewDatabaseName() => $"t_{Guid.NewGuid():N}";

    private static async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres", pooling: false));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
