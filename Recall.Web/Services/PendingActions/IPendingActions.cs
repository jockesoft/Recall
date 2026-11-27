namespace Recall.Web.Services.PendingActions;

/// <summary>
/// The pending-action cookie: what a signed-out visitor pressed, kept for
/// <see cref="PendingActions.Lifetime"/> in an HttpOnly cookie protected with
/// ASP.NET Data Protection (so it cannot be read or changed in the browser),
/// until they have signed in in this browser.
/// </summary>
public interface IPendingActions
{
    /// <summary>Stores <paramref name="action"/> in the cookie (replacing any earlier one) and counts the press. An invalid action is not stored.</summary>
    void Start(HttpContext context, PendingAction action);

    /// <summary>The pending action, when the cookie is there, intact, not expired and of an allowed kind; else null.</summary>
    PendingAction? Peek(HttpContext context);

    /// <summary>As <see cref="Peek"/>, and removes the cookie whatever it held.</summary>
    PendingAction? Take(HttpContext context);
}
