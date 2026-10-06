namespace Recall.Web.Infrastructure.Display;

/// <summary>The kinds of toast the layout can show (<c>_ToastMessages.cshtml</c>).</summary>
public enum ToastKind
{
    Success,
    Info,
    Warning,
    Error
}

/// <summary>
/// How long a toast stays before it closes itself: the one place that decides
/// it. The partial writes the answer on each toast as <c>data-toast-duration</c>
/// (milliseconds) and <c>js/tvdb-toasts.js</c> runs the countdown from that;
/// a toast without the attribute stays until it is closed.
/// </summary>
public static class ToastDurations
{
    /// <summary>A success or info toast that only says something.</summary>
    public static readonly TimeSpan Plain = TimeSpan.FromSeconds(5);

    /// <summary>A toast with something to press (Undo, Rate it, any later button): time to read it and decide.</summary>
    public static readonly TimeSpan WithAction = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Null means the toast never closes on its own: an error or a warning
    /// says something went wrong, and must not disappear while the user is
    /// looking elsewhere.
    /// </summary>
    public static TimeSpan? For(ToastKind kind, bool hasAction) => kind switch
    {
        ToastKind.Error or ToastKind.Warning => null,
        _ => hasAction ? WithAction : Plain
    };
}
