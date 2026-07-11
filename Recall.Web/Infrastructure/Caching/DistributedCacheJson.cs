using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Recall.Web.Infrastructure.Caching;

public sealed class DistributedCacheJson(IDistributedCache cache) : IDistributedCacheJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var payload = await cache.GetStringAsync(key, ct);
        if (payload is null) return default;
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        return cache.SetStringAsync(
            key,
            payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            ct);
    }

    public Task RemoveAsync(string key, CancellationToken ct = default) => cache.RemoveAsync(key, ct);
}