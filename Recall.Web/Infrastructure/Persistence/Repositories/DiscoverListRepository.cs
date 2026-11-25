using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class DiscoverListRepository(
    AppDbContext dbContext,
    ILogger<DiscoverListRepository> logger)
    : IDiscoverListRepository
{
    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    public async Task<IReadOnlyDictionary<string, DiscoverList>> GetAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
            return new Dictionary<string, DiscoverList>();

        var rows = await dbContext.DiscoverLists
            .AsNoTracking()
            .Where(x => keys.Contains(x.Key))
            .ToListAsync(cancellationToken);

        var lists = new Dictionary<string, DiscoverList>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            try
            {
                var titles = JsonSerializer.Deserialize<List<DiscoverTitle>>(row.Payload, JsonOptions);
                if (titles is not null)
                    lists[row.Key] = new DiscoverList(row.Key, titles, row.FetchedUtc);
            }
            catch (JsonException ex)
            {
                // An unreadable list is as good as none: its row is hidden, and the next fetch replaces it.
                logger.LogWarning(ex, "Discover list {Key} could not be read.", row.Key);
            }
        }

        return lists;
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> GetFetchedUtcAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.DiscoverLists
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Key, x => x.FetchedUtc, StringComparer.Ordinal, cancellationToken);
    }

    public async Task SaveAsync(
        string key, IReadOnlyList<DiscoverTitle> titles, DateTime fetchedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var payload = JsonSerializer.Serialize(titles, JsonOptions);

        if (await UpdateAsync(key, payload, titles.Count, fetchedUtc, cancellationToken))
            return;

        var entity = new DiscoverListEntity { Key = key, Payload = payload, ItemCount = titles.Count, FetchedUtc = fetchedUtc };
        dbContext.DiscoverLists.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another writer stored the list first (the job is single, so only
            // ever in a test or a second instance). Detach the losing row, or the
            // next save on this context would insert it again, and write ours over it.
            dbContext.Entry(entity).State = EntityState.Detached;
            await UpdateAsync(key, payload, titles.Count, fetchedUtc, cancellationToken);
        }
    }

    public Task<int> DeleteUnusedAsync(
        IReadOnlyCollection<string> keep, DateTime fetchedBefore, CancellationToken cancellationToken = default)
    {
        return dbContext.DiscoverLists
            .Where(x => !keep.Contains(x.Key) && x.FetchedUtc < fetchedBefore)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<bool> UpdateAsync(
        string key, string payload, int itemCount, DateTime fetchedUtc, CancellationToken cancellationToken)
    {
        var updated = await dbContext.DiscoverLists
            .Where(x => x.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Payload, payload)
                .SetProperty(x => x.ItemCount, itemCount)
                .SetProperty(x => x.FetchedUtc, fetchedUtc),
                cancellationToken);

        return updated > 0;
    }
}
