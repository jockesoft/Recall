namespace Recall.Web.Infrastructure.External.Omdb;

/// <summary>
/// Shared daily budget for live OMDb calls. Both <c>UpdateOmdbInfoTimer</c>
/// (proactive series enrichment) and Episodes/Details' on-demand per-episode
/// lookup must acquire a permit here before calling OMDb — a single enforced
/// cap across every caller, rather than each one assuming it owns the whole
/// quota.
/// </summary>
public interface IOmdbRequestBudget
{
    /// <summary>True if a live OMDb call may be made right now; false once today's budget is exhausted.</summary>
    bool TryAcquire();
}
