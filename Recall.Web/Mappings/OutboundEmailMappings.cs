using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Mappings;

public static class OutboundEmailMappings
{
    public static OutboundEmail ToDomain(this EmailEntity entity)
    {
        return new OutboundEmail
        {
            Id = entity.Id,
            Priority = entity.Priority,
            ToAddress = entity.ToAddress,
            Subject = entity.Subject,
            Body = entity.Body,
            SendAttempts = entity.SendAttempts,
            SentUtc = entity.SentUtc,
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = entity.UpdatedUtc
        };
    }

    public static EmailEntity ToEntity(this OutboundEmail domain)
    {
        return new EmailEntity
        {
            Id = domain.Id == Guid.Empty ? Guid.NewGuid() : domain.Id,
            Priority = domain.Priority,
            ToAddress = domain.ToAddress,
            Subject = domain.Subject,
            Body = domain.Body,
            SendAttempts = domain.SendAttempts,
            SentUtc = domain.SentUtc,
            CreatedUtc = domain.CreatedUtc == default ? DateTime.UtcNow : domain.CreatedUtc,
            UpdatedUtc = domain.UpdatedUtc == default ? DateTime.UtcNow : domain.UpdatedUtc
        };
    }
}
