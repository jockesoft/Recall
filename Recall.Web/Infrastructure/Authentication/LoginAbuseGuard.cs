using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Recall.Web.Infrastructure.Authentication;

/// <summary>
/// Two fixed-window limiters, both checked before a sign-in email is queued:
/// one keyed per email address (a day-long cap that survives many short
/// <see cref="LoginTokenOptions.ResendCooldownSeconds"/> windows), one shared
/// by everyone (an hourly cap that catches abuse spread across many
/// addresses or IPs). Registered as a singleton — state is per process, which
/// is fine for Recall's single-instance deployment; it resets on restart.
/// </summary>
public sealed class LoginAbuseGuard : ILoginAbuseGuard, IDisposable
{
    private readonly RateLimiter _siteWideLimiter;
    private readonly PartitionedRateLimiter<string> _perEmailLimiter;

    public LoginAbuseGuard(IOptions<LoginTokenOptions> options)
    {
        var settings = options.Value;

        _siteWideLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, settings.MaxSignInEmailsPerHourSiteWide),
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });

        _perEmailLimiter = PartitionedRateLimiter.Create<string, string>(email =>
            RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, settings.MaxRequestsPerEmailPerDay),
                Window = TimeSpan.FromDays(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    public bool TryAcquire(string normalizedEmail)
    {
        // Cheapest, least specific check first.
        using var siteWideLease = _siteWideLimiter.AttemptAcquire();
        if (!siteWideLease.IsAcquired)
            return false;

        using var emailLease = _perEmailLimiter.AttemptAcquire(normalizedEmail);
        return emailLease.IsAcquired;
    }

    public void Dispose()
    {
        _siteWideLimiter.Dispose();
        _perEmailLimiter.Dispose();
    }
}
