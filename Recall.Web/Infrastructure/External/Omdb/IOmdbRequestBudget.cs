namespace Recall.Web.Infrastructure.External.Omdb;

/// <summary>
/// Shared daily budget for live OMDb calls (the free tier allows 1,000 a day).
/// Every request that reaches OMDb must be covered by one permit:
/// <list type="bullet">
/// <item>each caller — the series and movie enrichment jobs, and
/// Episodes/Details' on-demand lookup — acquires one before calling the client;</item>
/// <item>the client's retry acquires another for itself (see
/// <c>ExternalHttpResilience.AddOmdbResilience</c>), because a retry is a second
/// real request.</item>
/// </list>
/// A single enforced cap across all of them, rather than each one assuming it
/// owns the whole quota.
/// </summary>
public interface IOmdbRequestBudget
{
    /// <summary>True if a live OMDb call may be made right now; false once today's budget is exhausted.</summary>
    bool TryAcquire();
}
