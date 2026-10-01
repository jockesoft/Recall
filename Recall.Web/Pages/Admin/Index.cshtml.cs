using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Pages.Admin;

[Authorize(Roles = Roles.Admin)]
public sealed class IndexModel(IAppUserRepository userRepository) : PageModel
{
    /// <summary>Total rows in <c>app_user</c> — every account that has ever signed in.</summary>
    public int RegisteredUserCount { get; private set; }

    /// <summary>How many of those accounts are admins.</summary>
    public int AdminCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var counts = await userRepository.GetCountsAsync(cancellationToken);

        RegisteredUserCount = counts.Total;
        AdminCount = counts.Admins;
    }
}
