using Recall.Web.Domain.Omdb;

namespace Recall.Web.Services.External.Omdb;

/// <summary>
/// Thin HTTP transport for the OMDb API (https://www.omdbapi.com/). No caching —
/// that lives in <see cref="Recall.Web.Infrastructure.Persistence.OmdbCache.IOmdbSnapshotStore"/>.
/// </summary>
public interface IOmdbApiClient
{
    /// <summary>
    /// Looks up a title by IMDb id (<c>?i=tt…</c>). Returns the parsed record on
    /// an OMDb "Response":"True", or <c>null</c> when OMDb has nothing for the id
    /// (its "Response":"False") — network/HTTP failures throw.
    /// </summary>
    Task<OmdbSeries?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken = default);
}
