using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Pages.Admin;

[Authorize(Roles = Roles.Admin)]
public sealed class IndexModel(AppDbContext dbContext) : PageModel
{
    /// <summary>Total rows in <c>app_user</c> — every account that has ever signed in.</summary>
    public int RegisteredUserCount { get; private set; }

    /// <summary>How many of those accounts are admins.</summary>
    public int AdminCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        RegisteredUserCount = await dbContext.AppUsers.CountAsync(cancellationToken);
        AdminCount = await dbContext.AppUsers.CountAsync(u => u.Role == UserRole.Admin, cancellationToken);
    }
}
