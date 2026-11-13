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

    /// <summary>
    /// Atomically checks and starts a user's resend cooldown: <c>true</c> when no
    /// cooldown was active (one now is, for <paramref name="cooldown"/>);
    /// <c>false</c> when a request for this user already started one that
    /// hasn't elapsed yet. Backed by in-memory state, so — unlike a
    /// read-then-write DB check — two concurrent calls for the same user can
    /// never both return <c>true</c>.
    /// </summary>
    bool TryStartResendCooldown(Guid userId, TimeSpan cooldown);

    /// <summary>
    /// Wrong sign-in codes entered for <paramref name="normalizedEmail"/> since
    /// its last sign-in email, whether or not the address has an account. The
    /// token's own count (in the database) is what stops guessing; this one
    /// makes an unknown address answer exactly like a known one, lockout
    /// included, so the code form cannot tell which addresses have accounts.
    /// </summary>
    int WrongCodeCount(string normalizedEmail);

    /// <summary>Counts one wrong code for the address and returns the new count.</summary>
    int RecordWrongCode(string normalizedEmail);

    /// <summary>Starts the address's count again: a new sign-in email went out, or the code worked.</summary>
    void ResetWrongCodes(string normalizedEmail);
}
