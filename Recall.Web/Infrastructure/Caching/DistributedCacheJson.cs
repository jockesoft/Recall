using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// The Redis tier: values stored as Brotli-compressed JSON (<see cref="CachePayload"/>).
/// Redis is only ever a copy of the Postgres snapshots, so this never lets it
/// fail a request: a value that cannot be read is a miss, and when Redis itself
/// fails (down, unreachable, timing out) a read is a miss and a write is skipped.
/// </summary>
/// <remarks>
/// After a failure Redis is left alone for <see cref="BypassFor"/>, so an outage
/// costs one failed call per half minute rather than a connection attempt (and
/// its timeout) on every read. One Warning is logged when a bypass starts;
/// calls skipped during it log nothing. The caller's own cancellation still
/// propagates.
/// </remarks>
public sealed class DistributedCacheJson(
    IDistributedCache cache,
    TimeProvider timeProvider,
    ILogger<DistributedCacheJson> logger) : IDistributedCacheJson
{
    public static readonly TimeSpan BypassFor = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    // UTC ticks until which Redis is not called. Read and written with Interlocked:
    // this is a singleton used by every request.
    private long _bypassUntilTicks;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (IsBypassed()) return default;

        byte[]? stored;
        try
        {
            stored = await cache.GetAsync(key, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            StartBypass(ex, "read", key);
            return default;
        }

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

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        if (IsBypassed()) return;

        var payload = CachePayload.Serialize(value, JsonOptions);
        try
        {
            await cache.SetAsync(
                key,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            StartBypass(ex, "write", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (IsBypassed()) return;

        try
        {
            await cache.RemoveAsync(key, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            StartBypass(ex, "remove", key);
        }
    }

    private bool IsBypassed() =>
        timeProvider.GetUtcNow().UtcTicks < Interlocked.Read(ref _bypassUntilTicks);

    private void StartBypass(Exception ex, string operation, string key)
    {
        var now = timeProvider.GetUtcNow();
        var previous = Interlocked.Exchange(ref _bypassUntilTicks, (now + BypassFor).UtcTicks);

        // Several requests can fail at once, and a long outage fails again every
        // time a bypass ends: only a failure with no bypass in the last half
        // minute starts a new outage and logs.
        if (previous <= now.UtcTicks - BypassFor.Ticks)
        {
            logger.LogWarning(ex,
                "Redis {Operation} failed for {CacheKey}; the cache is bypassed for {Seconds} s and reads go to the Postgres snapshots.",
                operation, key, BypassFor.TotalSeconds);
        }
    }
}
