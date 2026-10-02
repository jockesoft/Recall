namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_Avatar.cshtml</c>: the signed-in user's avatar, generated from
/// their name as initials on the theme's amber. Decorative — the name is always
/// written next to it, or the control around it carries a label.
/// </summary>
/// <param name="Name">The user's display name.</param>
/// <param name="Size">"sm" (32px, navbar), "md" (40px, phone menu) or "lg" (64px, profile).</param>
public sealed record AvatarModel(string? Name, string Size = "sm");
