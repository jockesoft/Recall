using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>One stored Discover list: its titles in TheTVDB's order, and when TheTVDB last answered for it.</summary>
public sealed record DiscoverList(string Key, IReadOnlyList<DiscoverTitle> Titles, DateTime FetchedUtc);

/// <summary>
/// Discover's lists (<c>discover_list</c>): written by the daily job, read by
/// the Discover page in one query. Shared metadata, not user data.
/// </summary>
public interface IDiscoverListRepository
{
    /// <summary>The lists of <paramref name="keys"/> that exist and can be read; a missing or unreadable one is left out.</summary>
    Task<IReadOnlyDictionary<string, DiscoverList>> GetAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>When each stored list was last fetched, by key: what the job decides from, without reading a payload.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetFetchedUtcAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a list, replacing the one with the same key.</summary>
    Task SaveAsync(string key, IReadOnlyList<DiscoverTitle> titles, DateTime fetchedUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the lists not in <paramref name="keep"/> that were last fetched
    /// before <paramref name="fetchedBefore"/>: last year's lists, and genres
    /// nobody's rows use any more. Returns how many went.
    /// </summary>
    Task<int> DeleteUnusedAsync(
        IReadOnlyCollection<string> keep, DateTime fetchedBefore, CancellationToken cancellationToken = default);
}
