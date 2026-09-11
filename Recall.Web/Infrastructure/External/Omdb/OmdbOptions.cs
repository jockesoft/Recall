namespace Recall.Web.Infrastructure.External.Omdb;

/// <summary>
/// Bound from the <c>Omdb</c> configuration section. The API key belongs in
/// user-secrets (dev) or environment variables (prod), never in appsettings.
/// Dev:  dotnet user-secrets set "Omdb:ApiKey" "&lt;key&gt;"
/// Prod: Omdb__ApiKey=&lt;key&gt;  (in .env.prod)
/// </summary>
public sealed class OmdbOptions
{
    public const string SectionName = "Omdb";

    public string BaseUrl { get; set; } = "https://www.omdbapi.com/";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Shared daily cap on live OMDb calls, enforced across every caller
    /// (UpdateOmdbInfoTimer's proactive series enrichment and Episodes/Details'
    /// on-demand per-episode lookups) via <see cref="IOmdbRequestBudget"/> — so
    /// the combined total can't exceed OMDb's real quota (1000/day on the free
    /// tier) and start failing both call sites for the rest of the day. Default
    /// leaves headroom below that limit.
    /// </summary>
    public int MaxRequestsPerDay { get; set; } = 900;
}
