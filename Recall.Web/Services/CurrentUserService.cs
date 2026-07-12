using System.Security.Claims;

namespace Recall.Web.Services;

public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public bool IsAuthenticated =>
        httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;

    public string? ExternalUserId =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public Guid? UserId =>
        Guid.TryParse(ExternalUserId, out var id) ? id : null;

    public string? Email =>
        FindFirstValue(ClaimTypes.Email);

    public string? DisplayName =>
        FindFirstValue("name") ??
        httpContextAccessor.HttpContext?.User.Identity?.Name;

    private string? FindFirstValue(string claimType) =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(claimType);
}