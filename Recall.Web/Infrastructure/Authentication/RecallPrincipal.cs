using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// The one place the auth cookie's claim set is defined — used when the cookie
/// is first issued (Account/Verify) and whenever <see cref="RecallCookieEvents"/>
/// rebuilds it from the user row, so the two can't drift apart.
/// </summary>
public static class RecallPrincipal
{
    public static ClaimsPrincipal Create(Guid userId, string username, string email, UserRole role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role.ToString())
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
