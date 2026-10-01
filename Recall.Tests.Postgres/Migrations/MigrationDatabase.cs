using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Recall.Web.Infrastructure.Persistence;

namespace Recall.Tests.Postgres.Migrations;

/// <summary>
/// An empty database that a test migrates step by step, so rows can be seeded
/// at the schema a data migration was written against. Seeding and assertions
/// use plain SQL: the current entity model does not match a historical schema.
/// </summary>
public sealed class MigrationDatabase : IAsyncDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly NpgsqlDataSource _dataSource;

    private MigrationDatabase(string database)
    {
        _dbContext = PostgresSuite.NewContext(database, pooling: false);
        _dataSource = NpgsqlDataSource.Create(PostgresSuite.ConnectionString(database, pooling: false));
    }

    public static async Task<MigrationDatabase> CreateAsync() =>
        new(await PostgresSuite.CreateEmptyDatabaseAsync());

    public AppDbContext DbContext => _dbContext;

    /// <summary>Applies migrations up to and including <paramref name="migration"/> (its class name).</summary>
    public Task MigrateToAsync(string migration) =>
        _dbContext.GetService<IMigrator>().MigrateAsync(migration);

    public Task MigrateToLatestAsync() => _dbContext.Database.MigrateAsync();

    public async Task ExecuteAsync(string sql, params object?[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params object?[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Inserts a user with the columns that exist at every migration these tests stop at.</summary>
    public async Task<Guid> InsertUserAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO app_user (id, user_name, email, created_utc, updated_utc)
            VALUES ($1, $2, $3, now(), now());
            """,
            id, $"user-{id:N}", $"{id:N}@example.com");
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    private NpgsqlCommand CreateCommand(string sql, object?[] parameters)
    {
        var command = _dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
        return command;
    }
}
