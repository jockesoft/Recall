namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// Removes every key of Recall's cache from Redis. <see cref="DistributedCacheJson"/>
/// calls it once when Redis is reachable again after an outage.
/// </summary>
public interface ICacheClearer
{
    /// <summary>Removes every key under the cache's prefix and returns how many there were. Throws when Redis cannot be reached.</summary>
    Task<long> ClearAsync(CancellationToken cancellationToken = default);
}
