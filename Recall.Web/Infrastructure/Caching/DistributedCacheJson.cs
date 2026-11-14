using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// The Redis tier: values stored as Brotli-compressed JSON (<see cref="CachePayload"/>).
/// Redis is only ever a copy of the Postgres snapshots, so a value that cannot
/// be read is a miss, not an error.
/// </summary>
public sealed class DistributedCacheJson(
    IDistributedCache cache,
    ILogger<DistributedCacheJson> logger) : IDistributedCacheJson
{
    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var stored = await cache.GetAsync(key, ct);
        if (stored is null) return default;

        try
        {
            if (CachePayload.TryDeserialize<T>(stored, JsonOptions, out var value))
                return value;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Redis value for {CacheKey} did not deserialize as {Type}; treating it as a miss.", key, typeof(T).Name);
            return default;
        }

        logger.LogWarning("Redis value for {CacheKey} is not in the cache payload format; treating it as a miss.", key);
        return default;
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) =>
        cache.SetAsync(
            key,
            CachePayload.Serialize(value, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) => cache.RemoveAsync(key, ct);
}
