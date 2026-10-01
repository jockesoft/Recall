namespace Recall.Web.Services;

public static class CurrentUserServiceExtensions
{
    /// <summary>
    /// The one "is someone signed in, and who" check for handlers on pages that
    /// are open to anonymous visitors (where <c>[Authorize]</c> can't do it for
    /// them). True only for an authenticated request that carries a usable user
    /// id; <paramref name="userId"/> is that id.
    /// </summary>
    public static bool TryGetUserId(this ICurrentUserService currentUser, out Guid userId)
    {
        if (currentUser.IsAuthenticated && currentUser.UserId is { } id)
        {
            userId = id;
            return true;
        }

        userId = Guid.Empty;
        return false;
    }
}
