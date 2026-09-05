namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// In-memory volumetric guard for the passwordless sign-in flow, layered on
/// top of the per-IP <c>[EnableRateLimiting]</c> policy on the login endpoint
/// and the per-account <see cref="LoginTokenOptions.ResendCooldownSeconds"/>
/// check. Neither of those stops a script that rotates IP addresses or waits
/// out the cooldown many times a day — this catches that.
/// </summary>
public interface ILoginAbuseGuard
{
    /// <summary>
    /// True if a sign-in request for <paramref name="normalizedEmail"/> is
    /// still within both the per-address daily cap and the site-wide hourly
    /// cap; false if either has been exhausted, in which case the caller
    /// should silently drop the request exactly like an allowlist miss.
    /// </summary>
    bool TryAcquire(string normalizedEmail);
}
