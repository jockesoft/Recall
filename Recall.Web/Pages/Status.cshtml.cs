using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Display;

namespace Recall.Web.Pages;

/// <summary>
/// The page shown for a response that ended with an error status and no body:
/// an unknown URL (404), a rejected form (400), a rate limit (429). It is
/// reached through <c>UseStatusCodePagesWithReExecute("/Status/{0}")</c> in
/// <c>Program.cs</c>, so the visitor stays on the URL they asked for and gets
/// the original status code with a page inside the normal layout.
/// </summary>
/// <remarks>
/// The re-executed request keeps its original method, so a failed POST arrives
/// here as a POST: hence no antiforgery check and no handler methods (a Razor
/// page without a matching handler simply renders).
/// </remarks>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class StatusModel : PageModel
{
    /// <summary>The status code from the route, or 404 for anything that is not an error code.</summary>
    public int Code =>
        int.TryParse(RouteData.Values["code"]?.ToString(), out var code) && code is >= 400 and <= 599
            ? code
            : StatusCodes.Status404NotFound;

    public (string Icon, string Heading, string Text) Message => Code switch
    {
        StatusCodes.Status404NotFound => (
            Icons.NotFound,
            "Page not found",
            "There is nothing at this address. The link may be old, or the address may have been mistyped."),
        StatusCodes.Status400BadRequest => (
            Icons.Problem,
            "That didn't go through",
            "The page you sent this from may have been open for too long. Go back, reload it and try again."),
        StatusCodes.Status403Forbidden => (
            Icons.Secure,
            "You don't have access to this page",
            "This part of Recall is limited to certain accounts."),
        StatusCodes.Status429TooManyRequests => (
            Icons.NotAiredYet,
            "Too many requests",
            "You've made a lot of requests in a short time. Wait a minute and try again."),
        _ => (
            Icons.Problem,
            "Something went wrong",
            "Sorry about that. The problem is on our side. Please try again in a little while.")
    };

    /// <summary>Sets the status code for a direct visit; a re-executed request already has it.</summary>
    public void SetStatusCode() => Response.StatusCode = Code;
}
