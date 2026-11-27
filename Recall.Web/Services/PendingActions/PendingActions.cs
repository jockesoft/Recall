using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Recall.Web.Services.Metrics;

namespace Recall.Web.Services.PendingActions;

/// <summary>
/// The cookie behind <see cref="IPendingActions"/>. Its value is
/// "v1|{kind}|{id}|{rating}|{issued ticks}|{stage}", protected with Data
/// Protection under its own purpose: a value that was changed, made for
/// another purpose or another key ring does not unprotect and counts as no
/// cookie. The browser drops it after 15 minutes, and the server checks the
/// issue time against the same 15 minutes, so a kept copy does not work later
/// either (moving to a later stage keeps the issue time). The kind is read by
/// its name and must be one of <see cref="PendingActionKind"/>; the stage
/// (<see cref="PendingStage"/>, missing on cookies from before it existed:
/// Pressed) only says which steps of the sign-up funnel have been counted for
/// this press. Nothing else about the action is stored.
/// </summary>
public sealed class PendingActions(
    IDataProtectionProvider dataProtection,
    ISiteMetrics metrics,
    TimeProvider timeProvider,
    ILogger<PendingActions> logger) : IPendingActions
{
    public const string CookieName = "Recall.Pending";

    /// <summary>How long a pending action waits for the sign-in.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private const string Version = "v1";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Recall.PendingAction.v1");

    public void Start(HttpContext context, PendingAction action)
    {
        if (!action.IsValid)
            return;

        Write(context, action, timeProvider.GetUtcNow(), PendingStage.Pressed);
        metrics.Increment(SiteMetricKeys.ActionClicked(action.Kind));
    }

    public PendingAction? Peek(HttpContext context) => ReadCookie(context)?.Action;

    public PendingAction? Take(HttpContext context)
    {
        var action = Peek(context);
        if (context.Request.Cookies.ContainsKey(CookieName))
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = context.Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Lax });
        return action;
    }

    public bool Advance(HttpContext context, PendingStage to)
    {
        if (ReadCookie(context) is not { } state || state.Stage != to - 1)
            return false;

        Write(context, state.Action, state.Issued, to);
        return true;
    }

    /// <summary>The action in a protected cookie value, or null for anything that is not a valid, unexpired one.</summary>
    public PendingAction? Read(string? protectedValue) => ReadState(protectedValue)?.Action;

    private PendingState? ReadCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? ReadState(value) : null;

    private void Write(HttpContext context, PendingAction action, DateTimeOffset issued, PendingStage stage)
    {
        var plain = string.Join('|',
            Version,
            action.Kind.ToString(),
            action.TvdbId.ToString(CultureInfo.InvariantCulture),
            action.Rating?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            issued.UtcTicks.ToString(CultureInfo.InvariantCulture),
            stage.ToString());

        context.Response.Cookies.Append(CookieName, _protector.Protect(plain), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            // Lax: the emailed link is a top-level navigation from the mail
            // client, and the cookie must come along with it.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = issued + Lifetime,
            IsEssential = true
        });
    }

    private PendingState? ReadState(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return null;

        string plain;
        try
        {
            plain = _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            logger.LogDebug("A pending-action cookie did not unprotect (changed, or from another key ring) and was ignored.");
            return null;
        }

        var parts = plain.Split('|');
        if (parts.Length is not (5 or 6) || parts[0] != Version)
            return null;

        // By name only: "3" is a defined value of the enum too, and is not allowed.
        if (!IsName(parts[1], out PendingActionKind kind))
            return null;

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;

        int? rating = null;
        if (parts[3].Length > 0)
        {
            if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return null;
            rating = value;
        }

        if (!long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return null;
        }

        var stage = PendingStage.Pressed;
        if (parts.Length == 6 && !IsName(parts[5], out stage))
            return null;

        var issued = new DateTimeOffset(ticks, TimeSpan.Zero);
        var age = timeProvider.GetUtcNow() - issued;
        if (age < TimeSpan.Zero || age > Lifetime)
            return null;

        var action = new PendingAction(kind, id, rating);
        return action.IsValid ? new PendingState(action, issued, stage) : null;
    }

    private static bool IsName<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _);

    private sealed record PendingState(PendingAction Action, DateTimeOffset Issued, PendingStage Stage);
}
