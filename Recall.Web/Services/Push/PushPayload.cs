namespace Recall.Web.Services.Push;

/// <summary>
/// Everything a push carries, and all of it: the notification's title and
/// body, the page a tap opens, and the icon. Serialised as JSON for the
/// service worker (<c>wwwroot/sw.js</c>), encrypted for the one browser.
/// </summary>
public sealed record PushPayload(string Title, string Body, string Url, string Icon)
{
    public const string AppIcon = "/images/icons/icon-192.png";
}
