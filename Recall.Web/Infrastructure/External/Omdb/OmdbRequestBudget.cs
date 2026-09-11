using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Recall.Web.Infrastructure.External.Omdb;

/// <summary>
/// A single fixed-window limiter shared by every OMDb caller. Registered as a
/// singleton — state is per process, which is fine for Recall's single-instance
/// deployment; it resets on restart (same trade-off <c>LoginAbuseGuard</c> makes).
/// </summary>
public sealed class OmdbRequestBudget : IOmdbRequestBudget, IDisposable
{
    private readonly RateLimiter _limiter;

    public OmdbRequestBudget(IOptions<OmdbOptions> options)
    {
        _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.Value.MaxRequestsPerDay),
            Window = TimeSpan.FromDays(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    public bool TryAcquire()
    {
        using var lease = _limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
