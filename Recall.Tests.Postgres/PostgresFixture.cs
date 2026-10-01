using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Tests.Postgres;

/// <summary>
/// Base class for a fixture that needs the real schema: one database per
/// fixture, cloned from the migrated template. Tests in a fixture share that
/// database, so each one works on its own user (<see cref="SeedUserAsync"/>)
/// or its own TheTVDB ids (<see cref="NextId"/>) rather than on a clean table.
/// </summary>
public abstract class PostgresFixture
{
    private static int _lastId = 100_000;

    private NpgsqlDataSource _dataSource = null!;

    protected string Database { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task CreateDatabaseAsync()
    {
        Database = await PostgresSuite.CreateMigratedDatabaseAsync();

        // Mirrors AddPostgres in InfrastructureServiceCollectionExtensions.
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(PostgresSuite.ConnectionString(Database));
        dataSourceBuilder.EnableDynamicJson();
        _dataSource = dataSourceBuilder.Build();
    }

    [OneTimeTearDown]
    public async Task DisposeDataSourceAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
    }

    /// <summary>Options configured the way the application configures them.</summary>
    protected DbContextOptions<AppDbContext> Options(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
            .ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));

        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);

        return builder.Options;
    }

    protected AppDbContext NewContext(params IInterceptor[] interceptors) => new(Options(interceptors));

    protected IDbContextFactory<AppDbContext> NewFactory(params IInterceptor[] interceptors) =>
        new ContextFactory(Options(interceptors));

    /// <summary>A TheTVDB id no other test in this run has used.</summary>
    protected static int NextId() => Interlocked.Increment(ref _lastId);

    protected async Task<Guid> SeedUserAsync(string? username = null)
    {
        var id = Guid.NewGuid();

        await using var db = NewContext();
        db.AppUsers.Add(new AppUserEntity
        {
            Id = id,
            Email = $"{id:N}@example.com",
            Username = username ?? $"user-{id:N}"
        });
        await db.SaveChangesAsync();

        return id;
    }

    /// <summary>Runs one statement and returns its first column of its first row.</summary>
    protected async Task<T?> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var command = _dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    protected async Task ExecuteAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
