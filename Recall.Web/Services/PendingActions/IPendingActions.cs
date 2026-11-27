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

    /// <summary>
    /// Moves the press on to <paramref name="to"/> when it is at the stage just
    /// before it, and says whether it did: true exactly once per press for each
    /// stage, which is how the action path of the sign-up funnel counts each
    /// step once per press (a reload of the sign-in page, or a second email,
    /// is not a second step).
    /// </summary>
    bool Advance(HttpContext context, PendingStage to);
}

/// <summary>How far a press has come on the way to signing in, for the funnel's counts.</summary>
public enum PendingStage
{
    /// <summary>Pressed on a Details page.</summary>
    Pressed = 0,

    /// <summary>The sign-in page has shown what will happen.</summary>
    Shown = 1,

    /// <summary>A sign-in email was sent from it.</summary>
    Sent = 2
}
