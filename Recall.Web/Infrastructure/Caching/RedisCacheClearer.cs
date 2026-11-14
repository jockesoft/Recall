using StackExchange.Redis;

namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// Removes every key under <see cref="TvdbCacheKeys.InstancePrefix"/> from the
/// database the cache uses (the connection string's default database): keys
/// are found with <c>SCAN</c> (StackExchange.Redis's <c>KeysAsync</c>, a page
/// at a time) and removed with <c>UNLINK</c> in batches, so Redis is never
/// blocked the way <c>KEYS</c> or <c>DEL</c> on many keys would block it, and
/// nothing outside the prefix is touched (no <c>FLUSHDB</c>/<c>FLUSHALL</c>).
/// </summary>
/// <remarks>
/// It has its own connection, made on first use (normally never: only after an
/// outage) and kept: the distributed cache's connection is private to
/// <c>RedisCache</c>. It is configured like the cache's
/// (<see cref="RedisConnection.Options"/>), so while Redis is unreachable a
/// clear fails at once instead of waiting; only the very first connection
/// attempt, if Redis is down then, waits up to <c>connectTimeout</c>.
/// </remarks>
public sealed class RedisCacheClearer(string connectionString, string prefix) : ICacheClearer, IAsyncDisposable
{
    private const int PageSize = 1000;
    private const int UnlinkBatch = 500;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnectionMultiplexer? _connection;

    public async Task<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        var connection = await ConnectAsync(cancellationToken);
        var database = connection.GetDatabase();
        long removed = 0;

        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            if (server.IsReplica) continue;

            var batch = new List<RedisKey>(UnlinkBatch);
            await foreach (var key in server.KeysAsync(database.Database, $"{prefix}*", PageSize).WithCancellation(cancellationToken))
            {
                batch.Add(key);
                if (batch.Count == UnlinkBatch)
                {
                    removed += await UnlinkAsync(database, batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                removed += await UnlinkAsync(database, batch);
        }

        return removed;
    }

    private static async Task<long> UnlinkAsync(IDatabase database, List<RedisKey> keys) =>
        (long)await database.ExecuteAsync("UNLINK", keys.Cast<object>().ToArray());

    private async Task<IConnectionMultiplexer> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } existing) return existing;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            // AbortOnConnectFail is false (RedisConnection.Options): an unreachable
            // Redis gives a connection that keeps reconnecting in the background,
            // and the commands above fail fast until it does.
            return _connection ??= await ConnectionMultiplexer.ConnectAsync(RedisConnection.Options(connectionString));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        _connectLock.Dispose();
    }
}
