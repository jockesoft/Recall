namespace Recall.Web.Infrastructure.Display;

/// <summary>
/// Every icon the site uses, as the full CSS class for a Phosphor icon
/// (<c>wwwroot/lib/phosphor-icons</c>, regular and fill weights). Markup writes
/// <c>&lt;i class="@Icons.Series" aria-hidden="true"&gt;&lt;/i&gt;</c> rather than
/// a literal class, so one meaning has one icon everywhere and a new icon is
/// added in one place. Icons are decorative: the text or the control's
/// <c>aria-label</c> carries the meaning.
/// </summary>
public static class Icons
{
    // Content types
    public const string Series = "ph ph-television-simple";
    public const string Movie = "ph ph-film-strip";

    // Actions and state
    public const string Like = "ph ph-heart";
    public const string Liked = "ph-fill ph-heart";
    public const string Check = "ph ph-check";
    public const string Star = "ph-fill ph-star";
    public const string Send = "ph ph-paper-plane-tilt";
    public const string Search = "ph ph-magnifying-glass";
    public const string NotAiredYet = "ph ph-clock";
    public const string Award = "ph ph-medal";
    public const string Add = "ph ph-plus";
    public const string Watchlist = "ph ph-bookmark-simple";
    public const string OnWatchlist = "ph-fill ph-bookmark-simple";
    public const string More = "ph ph-dots-three";
    public const string SignIn = "ph ph-sign-in";
    public const string NoImage = "ph ph-image";
    public const string Unsupported = "ph ph-prohibit";
    public const string Upload = "ph ph-upload-simple";
    public const string SignOut = "ph ph-sign-out";
    public const string Mail = "ph ph-envelope-simple";

    // Navigation
    public const string Back = "ph ph-arrow-left";
    public const string Next = "ph ph-arrow-right";
    public const string External = "ph ph-arrow-up-right";
    public const string Forward = "ph ph-caret-right";
    public const string Expand = "ph ph-caret-down";
    public const string Menu = "ph ph-list";
    public const string Close = "ph ph-x";

    // Notifications and feedback
    public const string Bell = "ph ph-bell";
    public const string BellFilled = "ph-fill ph-bell";
    public const string Success = "ph ph-check-circle";
    public const string Error = "ph ph-warning";
    public const string Info = "ph ph-info";
    public const string Invalid = "ph ph-x";

    // People and roles
    public const string Member = "ph ph-user";
    public const string Administrator = "ph ph-shield-star";
    public const string AdminPanel = "ph ph-gauge";

    // Marketing and empty states
    public const string Track = "ph ph-list-checks";
    public const string Favorites = "ph-fill ph-heart";
    public const string Secure = "ph ph-shield-check";
    public const string NoSchedule = "ph ph-broadcast";
    public const string EmptyLibrary = "ph ph-books";
    public const string NoNotifications = "ph ph-bell-slash";
    public const string NotFound = "ph ph-compass";
    public const string Problem = "ph ph-plugs";
    public const string Facebook = "ph ph-facebook-logo";
}
