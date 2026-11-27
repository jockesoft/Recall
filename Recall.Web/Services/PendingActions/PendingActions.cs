using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Recall.Web.Services.Metrics;

namespace Recall.Web.Services.PendingActions;

/// <summary>
/// The cookie behind <see cref="IPendingActions"/>. Its value is
/// "v1|{kind}|{id}|{rating}|{issued ticks}", protected with Data Protection
/// under its own purpose: a value that was changed, made for another purpose
/// or another key ring does not unprotect and counts as no cookie. The
/// browser drops it after 15 minutes, and the server checks the issue time
/// against the same 15 minutes, so a kept copy does not work later either.
/// The kind is read by its name and must be one of
/// <see cref="PendingActionKind"/>; nothing else about the action is stored.
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

        var issued = timeProvider.GetUtcNow();
        var plain = string.Join('|',
            Version,
            action.Kind.ToString(),
            action.TvdbId.ToString(CultureInfo.InvariantCulture),
            action.Rating?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            issued.UtcTicks.ToString(CultureInfo.InvariantCulture));

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

        metrics.Increment(SiteMetricKeys.ActionClicked(action.Kind));
    }

    public PendingAction? Peek(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) ? Read(value) : null;

    public PendingAction? Take(HttpContext context)
    {
        var action = Peek(context);
        if (context.Request.Cookies.ContainsKey(CookieName))
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = context.Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Lax });
        return action;
    }

    /// <summary>The action in a protected cookie value, or null for anything that is not a valid, unexpired one.</summary>
    public PendingAction? Read(string? protectedValue)
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
        if (parts.Length != 5 || parts[0] != Version)
            return null;

        // By name only: "3" is a defined value of the enum too, and is not allowed.
        if (!Enum.TryParse<PendingActionKind>(parts[1], ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind)
            || int.TryParse(parts[1], out _))
        {
            return null;
        }

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

        var age = timeProvider.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero);
        if (age < TimeSpan.Zero || age > Lifetime)
            return null;

        var action = new PendingAction(kind, id, rating);
        return action.IsValid ? action : null;
    }
}
