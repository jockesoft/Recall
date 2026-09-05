namespace Recall.Web.Services;

public interface ICurrentUserService
{
    bool IsAuthenticated { get; }
    string? ExternalUserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    Guid? UserId { get; }

    /// <summary>The signed-in user's role name (matches <c>UserRole</c>), or null when anonymous.</summary>
    string? Role { get; }

    /// <summary>True when the signed-in user is in <paramref name="role"/> (e.g. <c>Roles.Admin</c>).</summary>
    bool IsInRole(string role);

    /// <summary>Convenience for <c>IsInRole(Roles.Admin)</c>.</summary>
    bool IsAdmin { get; }
}
