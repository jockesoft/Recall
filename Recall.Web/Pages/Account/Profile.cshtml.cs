using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class ProfileModel(ICurrentUserService currentUser) : PageModel
{
    public string DisplayName => currentUser.DisplayName ?? "—";

    public string Email => currentUser.Email ?? "—";

    public void OnGet()
    {
    }
}
