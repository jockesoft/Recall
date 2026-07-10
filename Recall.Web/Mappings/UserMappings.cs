using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Mappings;

public static class UserMappings
{
    public static UserItem ToDomain(this AppUserEntity e) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        Username = e.Username,
        Email = e.Email,
        CreatedUtc = e.CreatedUtc,
        UpdatedUtc = e.UpdatedUtc
    };
}