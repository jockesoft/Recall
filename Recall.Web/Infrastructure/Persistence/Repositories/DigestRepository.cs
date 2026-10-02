using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Mappings;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class DigestRepository(AppDbContext dbContext) : IDigestRepository
{
    public async Task<IReadOnlyList<DigestRecipient>> GetDueRecipientsAsync(
        DateOnly periodStart,
        int max,
        CancellationToken cancellationToken = default)
    {
        if (max <= 0)
            return [];

        return await dbContext.AppUsers
            .AsNoTracking()
            .Where(user => user.DigestOptedInUtc != null
                           && !dbContext.DigestSends.Any(sent => sent.UserId == user.Id && sent.PeriodStart == periodStart))
            .OrderBy(user => user.DigestOptedInUtc)
            .ThenBy(user => user.Id)
            .Take(max)
            .Select(user => new DigestRecipient(user.Id, user.Email, user.Username))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RecordAsync(
        Guid userId,
        DateOnly periodStart,
        DigestSendStatus status,
        OutboundEmail? email,
        CancellationToken cancellationToken = default)
    {
        if (await dbContext.DigestSends.AnyAsync(x => x.UserId == userId && x.PeriodStart == periodStart, cancellationToken))
            return false;

        var ledger = new DigestSendEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PeriodStart = periodStart,
            Status = status,
            CreatedUtc = DateTime.UtcNow
        };
        var queued = email?.ToEntity();

        dbContext.DigestSends.Add(ledger);
        if (queued is not null)
            dbContext.Emails.Add(queued);

        try
        {
            // One SaveChanges is one transaction: the ledger row and the email go in together or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another run recorded this week between the check and the insert.
            // Take both rows out of the context, or the next SaveChanges retries them.
            dbContext.Entry(ledger).State = EntityState.Detached;
            if (queued is not null)
                dbContext.Entry(queued).State = EntityState.Detached;

            return false;
        }
    }
}
