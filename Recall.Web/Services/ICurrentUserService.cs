namespace Recall.Web.Services;

public interface ICurrentUserService
{
    bool IsAuthenticated { get; }
    string? ExternalUserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    Guid? UserId { get; }
}