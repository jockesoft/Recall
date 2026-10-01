namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// An entity whose <c>created_utc</c> / <c>updated_utc</c> columns are
/// maintained by <see cref="AppDbContext"/>: both are set when the row is
/// inserted through <c>SaveChanges</c>, and <see cref="UpdatedUtc"/> again on
/// every tracked update. Don't set either by hand — the value is overwritten.
///
/// Bulk statements (<c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>) and
/// raw SQL bypass the change tracker, so they must set <c>UpdatedUtc</c>
/// themselves.
/// </summary>
public interface IHasAuditTimestamps
{
    DateTime CreatedUtc { get; set; }
    DateTime UpdatedUtc { get; set; }
}
