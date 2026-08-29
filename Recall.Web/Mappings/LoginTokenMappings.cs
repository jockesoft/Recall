using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Mappings;

public static class LoginTokenMappings
{
    public static LoginToken ToDomain(this LoginTokenEntity entity)
    {
        return new LoginToken
        {
            Id = entity.Id,
            UserId = entity.UserId,
            TokenHash = entity.TokenHash,
            ExpiresUtc = entity.ExpiresUtc,
            ConsumedUtc = entity.ConsumedUtc,
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = entity.UpdatedUtc
        };
    }

    public static LoginTokenEntity ToEntity(this LoginToken domain)
    {
        return new LoginTokenEntity
        {
            Id = domain.Id == Guid.Empty ? Guid.NewGuid() : domain.Id,
            UserId = domain.UserId,
            TokenHash = domain.TokenHash,
            ExpiresUtc = domain.ExpiresUtc,
            ConsumedUtc = domain.ConsumedUtc,
            CreatedUtc = domain.CreatedUtc == default ? DateTime.UtcNow : domain.CreatedUtc,
            UpdatedUtc = domain.UpdatedUtc == default ? DateTime.UtcNow : domain.UpdatedUtc
        };
    }
}
