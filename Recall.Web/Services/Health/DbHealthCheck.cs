using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Recall.Web.Services.Health;

/// <summary>
/// The check behind <c>/health</c>: reports Healthy only when the app can reach
/// Postgres (a bare <c>SELECT 1</c> via <see cref="IDbHealthProbe"/>). Tagged
/// <c>ready</c> so a readiness-only endpoint can filter to it later.
/// </summary>
public sealed class DbHealthCheck(IDbHealthProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await probe.IsDatabaseReachableAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Database reachable.")
            : HealthCheckResult.Unhealthy("Database unreachable.");
    }
}
