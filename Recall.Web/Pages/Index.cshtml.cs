using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Recall.Web.Pages;

/// <summary>
/// Public marketing landing page — the only page most anonymous visitors (and
/// search engines) ever see, since everything past sign-in is <see cref="AuthorizeAttribute"/>-gated.
/// A signed-in visitor is sent straight to <see cref="DashboardModel"/> instead.
/// </summary>
[AllowAnonymous]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true
            ? RedirectToPage("/Dashboard")
            : Page();
}
