using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;

namespace Recall.Web.Services.WatchTracking;

public enum AddSeriesOutcome
{
    Added,
    AlreadyInLibrary,

    /// <summary>TheTVDB has no such series: nothing was added.</summary>
    NotFound
}

/// <summary>What "Add to library" did, and the series' name for the toast.</summary>
public sealed record AddSeriesResult(AddSeriesOutcome Outcome, string? SeriesName = null);

/// <summary>
/// Puts a series in the user's library and takes it out again: the one way
/// "Add to library" is done, from Series Details and from Discover. (A mark
/// adds a series by itself, in <see cref="WatchProgressService"/>.)
/// </summary>
public interface ISeriesLibraryService
{
    /// <summary>
    /// Adds the series, with its name, overview, poster and first air date from
    /// the aggregate: cached, or fetched from TheTVDB (a few requests) when no
    /// one has opened it yet, which the Library would need anyway.
    /// </summary>
    Task<AddSeriesResult> AddAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the series out of the library (the row only: watches, likes and
    /// ratings stay). False when it was not there.
    /// </summary>
    Task<bool> RemoveAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken = default);
}

public sealed class SeriesLibraryService(
    ITrackedSeriesRepository trackedSeriesRepository,
    ITheTvDbService theTvDbService)
    : ISeriesLibraryService
{
    public async Task<AddSeriesResult> AddAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken = default)
    {
        if (await trackedSeriesRepository.ExistsAsync(userId, seriesTvdbId, cancellationToken))
            return new AddSeriesResult(AddSeriesOutcome.AlreadyInLibrary);

        var series = await theTvDbService.GetSeriesByIdAsync(seriesTvdbId, cancellationToken);
        if (series is null)
            return new AddSeriesResult(AddSeriesOutcome.NotFound);

        // False: a concurrent request added it first.
        return await trackedSeriesRepository.AddAsync(TrackedSeriesMappings.FromTvDbDetails(userId, series), cancellationToken)
            ? new AddSeriesResult(AddSeriesOutcome.Added, series.Name)
            : new AddSeriesResult(AddSeriesOutcome.AlreadyInLibrary, series.Name);
    }

    public async Task<bool> RemoveAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken = default)
    {
        var tracked = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(userId, seriesTvdbId, cancellationToken);
        if (tracked is null)
            return false;

        await trackedSeriesRepository.RemoveAsync(userId, tracked.Id, cancellationToken);
        return true;
    }
}
