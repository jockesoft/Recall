using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Internal;
using Recall.Web.Mappings;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class EmailRepository(AppDbContext dbContext) : IEmailRepository
{
    public async Task AddAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        dbContext.Emails.Add(email.ToEntity());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboundEmail>> GetPendingAsync(
        int maxCount,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Emails
            .AsNoTracking()
            .Where(x => x.SentUtc == null && x.SendAttempts < maxAttempts)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedUtc)
            .Take(maxCount)
            .Select(x => x.ToDomain())
            .ToListAsync(cancellationToken);
    }

    public async Task MarkSentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await dbContext.Emails
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.SentUtc, DateTime.UtcNow)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);
    }

    public async Task RecordFailedAttemptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await dbContext.Emails
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.SendAttempts, x => x.SendAttempts + 1)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);
    }
}
