using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;

namespace Recall.Web.Services.Health;

public sealed class DbHealthProbe(AppDbContext dbContext, ILogger<DbHealthProbe> logger) : IDbHealthProbe
{
    /// <summary>A well-behaved <c>SELECT 1</c> should come back in single-digit ms.</summary>
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(250);

    public async Task<bool> IsDatabaseReachableAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            // CanConnectAsync issues a bare "SELECT 1" on Npgsql — no table
            // access, no locks — and folds connection failures into `false`
            // rather than throwing.
            var reachable = await dbContext.Database.CanConnectAsync(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (!reachable)
            {
                logger.LogWarning("DB health probe: database did not answer SELECT 1 ({ElapsedMs:F0} ms).", elapsed.TotalMilliseconds);
            }
            else if (elapsed > SlowThreshold)
            {
                logger.LogWarning("DB health probe: slow SELECT 1 ({ElapsedMs:F0} ms).", elapsed.TotalMilliseconds);
            }

            return reachable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "DB health probe: SELECT 1 failed.");
            return false;
        }
    }
}
