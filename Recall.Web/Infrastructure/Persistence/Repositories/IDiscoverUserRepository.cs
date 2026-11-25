using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>The titles a user already knows, which Discover leaves out of every row.</summary>
public sealed record KnownTitles(IReadOnlySet<int> SeriesIds, IReadOnlySet<int> MovieIds)
{
    public static KnownTitles None { get; } = new(new HashSet<int>(), new HashSet<int>());

    public bool Contains(DiscoverKind kind, int tvdbId) =>
        (kind == DiscoverKind.Movie ? MovieIds : SeriesIds).Contains(tvdbId);
}

/// <summary>What Discover reads and writes for one user: what they already know, and what they hid.</summary>
public interface IDiscoverUserRepository
{
    /// <summary>
    /// Every series and movie the user already knows, in one query: in the
    /// library (stopped series too), any episode watched (a series removed
    /// from the library included), on the watchlist, watched, liked or rated
    /// (a series or a movie, not an episode), or hidden from Discover.
    /// </summary>
    Task<KnownTitles> GetKnownTitlesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>"Not interested": hides the title from the user's Discover. False when it was already hidden.</summary>
    Task<bool> HideAsync(Guid userId, DiscoverKind kind, int tvdbId, CancellationToken cancellationToken = default);

    /// <summary>The Undo of <see cref="HideAsync"/>. False when it was not hidden.</summary>
    Task<bool> UnhideAsync(Guid userId, DiscoverKind kind, int tvdbId, CancellationToken cancellationToken = default);
}
