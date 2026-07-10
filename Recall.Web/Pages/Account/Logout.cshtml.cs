using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Recall.Web.Pages.Account;

public sealed class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPost()
    {


        return RedirectToPage("/Index");
    }
}