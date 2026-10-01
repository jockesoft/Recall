using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Recall.Web.Pages.Account;

public sealed class LogoutModel : PageModel
{
    // GET changes nothing: signing out on a plain link would let any page on
    // the web log a visitor out with an <img> tag. The nav bar and the profile
    // page both post a form (which carries the antiforgery token).
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Index");
    }
}
