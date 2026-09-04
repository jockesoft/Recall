namespace Recall.Web.Services.Health;

/// <summary>
/// Cheap "is the database reachable?" probe for external uptime monitoring.
/// </summary>
public interface IDbHealthProbe
{
    /// <summary>
    /// Runs the lightest possible query (<c>SELECT 1</c>, no table access, no
    /// locks) against Postgres. Returns <c>true</c> when the database answered,
    /// <c>false</c> on any connectivity failure or error. Never throws (other
    /// than <see cref="OperationCanceledException"/>).
    /// </summary>
    Task<bool> IsDatabaseReachableAsync(CancellationToken cancellationToken = default);
}
