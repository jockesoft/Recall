using StackExchange.Redis;

namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// The StackExchange.Redis settings behind the distributed cache.
/// </summary>
public static class RedisConnection
{
    /// <summary>
    /// The connection string's settings with <see cref="BacklogPolicy.FailFast"/>:
    /// while there is no connection, a command fails at once instead of
    /// waiting in the client's backlog for a reconnect. With the default
    /// policy every cache call took 5 seconds (the async timeout) while Redis
    /// was down, measured 2026-11-14; failing fast lets
    /// <see cref="DistributedCacheJson"/> fall through to Postgres immediately.
    /// For a cache that is the right trade: a command lost in a reconnect is
    /// only a miss. The first connection attempt after start still waits up to
    /// <c>connectTimeout</c> (5 s by default) when Redis is not there.
    /// </summary>
    public static ConfigurationOptions Options(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return options;
    }
}
