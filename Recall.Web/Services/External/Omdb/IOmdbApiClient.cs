using Recall.Web.Domain.Omdb;

namespace Recall.Web.Services.External.Omdb;

/// <summary>
/// Thin HTTP transport for the OMDb API (https://www.omdbapi.com/). No caching —
/// that lives in <see cref="Recall.Web.Infrastructure.Persistence.OmdbCache.IOmdbSnapshotStore"/>.
/// </summary>
public interface IOmdbApiClient
{
    // Each lookup is by IMDb id (<c>?i=tt…</c>) and returns the parsed record on
    // an OMDb "Response":"True", or null when OMDb has nothing for the id (its
    // "Response":"False"). Network/HTTP failures throw.

    /// <summary>Looks up a series. No <c>&amp;type=</c> filter is sent, so a mini-series or anything else OMDb files under that id is returned as-is.</summary>
    Task<OmdbSeries?> GetSeriesAsync(string imdbId, CancellationToken cancellationToken = default);

    /// <summary>Looks up a movie (<c>&amp;type=movie</c>).</summary>
    Task<OmdbMovie?> GetMovieAsync(string imdbId, CancellationToken cancellationToken = default);

    /// <summary>Looks up a single episode (<c>&amp;type=episode</c>).</summary>
    Task<OmdbEpisode?> GetEpisodeAsync(string imdbId, CancellationToken cancellationToken = default);
}
