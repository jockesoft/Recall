using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Caching;

namespace Recall.Tests.Infrastructure.Caching;

/// <summary>
/// An in-memory stand-in for Redis that records what it is asked and can be
/// taken down (<see cref="Down"/>): every call then throws, as the Redis client
/// does when it has no connection.
/// </summary>
internal sealed class SwitchableCache : IDistributedCache
{
    private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly HashSet<string> _keys = [];

    public bool Down { get; set; }
    public int Calls { get; private set; }
    public List<string> Reads { get; } = [];
    public List<TimeSpan> Lifetimes { get; } = [];
    public IReadOnlyCollection<string> Keys => _keys;

    /// <summary>A clearer of this cache: removes every key, or throws while it is down.</summary>
    public SwitchableCacheClearer Clearer => field ??= new SwitchableCacheClearer(this);

    public byte[]? Get(string key) => throw new NotSupportedException();
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
    public void Refresh(string key) => throw new NotSupportedException();
    public void Remove(string key) => throw new NotSupportedException();
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Calls++;
        ThrowIfDown();
        Reads.Add(key);
        return _inner.GetAsync(key, token);
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Calls++;
        ThrowIfDown();
        _keys.Add(key);
        if (options.AbsoluteExpirationRelativeToNow is { } ttl) Lifetimes.Add(ttl);
        return _inner.SetAsync(key, value, options, token);
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Calls++;
        ThrowIfDown();
        _keys.Remove(key);
        return _inner.RemoveAsync(key, token);
    }

    internal async Task<long> RemoveEverythingAsync()
    {
        ThrowIfDown();
        var keys = _keys.ToList();
        foreach (var key in keys) await _inner.RemoveAsync(key);
        _keys.Clear();
        return keys.Count;
    }

    private void ThrowIfDown()
    {
        if (Down) throw new InvalidOperationException("It was not possible to connect to the redis server(s).");
    }
}

/// <summary>Clears a <see cref="SwitchableCache"/> and counts the clears that succeeded and failed.</summary>
internal sealed class SwitchableCacheClearer(SwitchableCache cache) : ICacheClearer
{
    public int Clears { get; private set; }
    public int FailedClears { get; private set; }

    /// <summary>When set, a clear waits for it before removing anything.</summary>
    public Task? Gate { get; set; }

    public async Task<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        if (Gate is { } gate) await gate;
        try
        {
            var removed = await cache.RemoveEverythingAsync();
            Clears++;
            return removed;
        }
        catch
        {
            FailedClears++;
            throw;
        }
    }
}
