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
                    // The content has done its job once delivered. For a sign-in
                    // email it holds the raw magic-link token, which must not sit
                    // readable in the table (or in its backups) afterwards.
                    .SetProperty(x => x.Body, string.Empty)
                    .SetProperty(x => x.HtmlBody, (string?)null)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);
    }

    public async Task RecordFailedAttemptAsync(Guid id, int maxAttempts, CancellationToken cancellationToken = default)
    {
        // One statement: every right-hand side sees the row as it was before the
        // update, so "SendAttempts + 1" is the count this failure brings it to.
        // On the attempt that exhausts the limit the message will never be
        // picked up again, so its content goes the same way a sent one's does
        // (see MarkSentAsync) instead of sitting in the table indefinitely.
        await dbContext.Emails
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Body, x => x.SendAttempts + 1 >= maxAttempts ? string.Empty : x.Body)
                    .SetProperty(x => x.HtmlBody, x => x.SendAttempts + 1 >= maxAttempts ? null : x.HtmlBody)
                    .SetProperty(x => x.SendAttempts, x => x.SendAttempts + 1)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);
    }
}
