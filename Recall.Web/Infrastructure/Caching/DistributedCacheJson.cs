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
/// <para>
/// After a failure Redis is left alone for <see cref="BypassFor"/>, so an outage
/// costs one failed call per half minute rather than a connection attempt (and
/// its timeout) on every read. One Warning is logged when a bypass starts;
/// calls skipped during it log nothing. The caller's own cancellation still
/// propagates.
/// </para>
/// <para>
/// When the bypass after an outage ends, the first caller clears every key of
/// the cache (<see cref="ICacheClearer"/>) before anything is read from or
/// written to Redis, and logs one Information line with the number removed.
/// While Redis was skipped, refreshes rewrote Postgres rows without their
/// Redis entries; an entry from before or during the outage could otherwise
/// outlive such a refresh for the rest of its week. The clear is also the test
/// of whether Redis is back: if it fails, the bypass starts again and the next
/// caller after it tries again. Other callers treat Redis as bypassed while it
/// runs. Afterwards the cache refills from Postgres as pages are read.
/// </para>
/// </remarks>
public sealed class DistributedCacheJson(
    IDistributedCache cache,
    ICacheClearer clearer,
    TimeProvider timeProvider,
    ILogger<DistributedCacheJson> logger) : IDistributedCacheJson
{
    public static readonly TimeSpan BypassFor = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    // All state is read and written with Interlocked: this is a singleton used
    // by every request.

    // UTC ticks until which Redis is not called.
    private long _bypassUntilTicks;

    // Counts failures; the clear records the count it ran for. A failure while
    // a clear runs (a call already in flight) leaves them unequal, so the
    // cache is cleared again after that bypass too.
    private long _failures;
    private long _clearedThrough;

    // 1 while a clear runs.
    private int _clearing;

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (!await CanUseRedisAsync(ct)) return default;

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
        if (!await CanUseRedisAsync(ct)) return;

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
        if (!await CanUseRedisAsync(ct)) return;

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

    /// <summary>
    /// False while Redis is bypassed or being cleared. True when it may be
    /// used, after clearing it first if it has failed since the last clear.
    /// </summary>
    private async ValueTask<bool> CanUseRedisAsync(CancellationToken ct)
    {
        if (IsBypassed()) return false;

        if (Interlocked.Read(ref _failures) == Interlocked.Read(ref _clearedThrough)) return true;

        if (Interlocked.CompareExchange(ref _clearing, 1, 0) != 0) return false;
        try
        {
            // Read again under the guard: another caller may have finished a
            // clear since the check above. Only the guard's holder writes
            // _clearedThrough, so it never moves backwards.
            var failures = Interlocked.Read(ref _failures);
            if (failures == Interlocked.Read(ref _clearedThrough)) return true;

            long removed;
            try
            {
                removed = await clearer.ClearAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                StartBypass(ex, "clear", TvdbCacheKeys.InstancePrefix + "*");
                return false;
            }

            Interlocked.Exchange(ref _clearedThrough, failures);
            logger.LogInformation(
                "Redis is reachable again after an outage; removed {Count} cache key(s) written before or during it.",
                removed);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _clearing, 0);
        }
    }

    private void StartBypass(Exception ex, string operation, string key)
    {
        Interlocked.Increment(ref _failures);
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
