using Recall.Web.Infrastructure.Display;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_Avatar.cshtml</c>: the signed-in user's avatar, generated from
/// their name as initials on the theme's amber. Decorative — the name is always
/// written next to it, or the control around it carries a label.
/// </summary>
/// <param name="Name">The user's display name.</param>
/// <param name="Size">"sm" (32px, navbar), "md" (40px, phone menu) or "lg" (64px, profile).</param>
public sealed record AvatarModel(string? Name, string Size = "sm")
{
    /// <summary>
    /// The accessible name of the navbar's account menu, whose only visible
    /// content is the avatar: "DU, account menu". It starts with the initials
    /// the control shows (WCAG 2.5.3, label in name): someone using speech
    /// input says what they see ("click DU"), and someone using a screen reader
    /// with some sight hears what is on screen before what it does. Hiding the
    /// initials from assistive technology does not meet the criterion, which is
    /// about the visible text. Without initials ("?", no usable name) it is
    /// just "Account menu".
    /// </summary>
    public static string AccountMenuLabel(string? name) =>
        Initials.Of(name) is var initials && initials != "?" ? $"{initials}, account menu" : "Account menu";
}
