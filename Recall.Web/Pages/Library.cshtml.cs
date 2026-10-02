using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages;

/// <summary>
/// One card per (tracked) series or (watched) movie, sorted into three
/// buckets:
/// <list type="bullet">
/// <item><b>Watching</b> — has at least one released episode not yet watched.</item>
/// <item><b>Up to date</b> — caught up on every released episode, and the series hasn't ended.</item>
/// <item><b>Watched</b> — caught up AND ended (series), or marked watched (movies).</item>
/// </list>
/// </summary>
public sealed record LibraryCardItem(
    SearchResultType Type,
    int TvdbId,
    string Name,
    string? ImageUrl,
    DateOnly? FirstAired,
    int WatchedEpisodes,
    int ReleasedEpisodes,
    bool IsLiked,
    string? Caption,
    string? ProgressText = null);

/// <summary>The Library's four sections, in the order the page shows them.</summary>
public enum LibrarySection
{
    Watching,
    ToWatch,
    UpToDate,
    Watched
}

/// <summary>
/// One section of the Library with what is in it. <paramref name="Slug"/> is its
/// name in a URL: the anchor on the full page (<c>/Library#watching</c>) and
/// the value of <c>?section=</c> for the view that shows only this section.
/// </summary>
public sealed record LibrarySectionView(LibrarySection Section, string Slug, string Title, IReadOnlyList<LibraryCardItem> Items);

[Authorize]
public sealed class LibraryModel(
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    IWatchProgressService watchProgressService,
    IEpisodeWatchRepository episodeWatchRepository,
    IMovieWatchRepository movieWatchRepository,
    ITrackedMovieRepository trackedMovieRepository,
    ITheTvDbService theTvDbService,
    ILikeRepository likeRepository,
    TimeProvider timeProvider,
    ILogger<LibraryModel> logger)
    : PageModel
{
    /// <summary>Today's date in UTC, for the card captions' date format.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    public IReadOnlyList<LibraryCardItem> Watching { get; private set; } = [];

    /// <summary>Movies on the watchlist — wanted, not yet watched. Most recently added first.</summary>
    public IReadOnlyList<LibraryCardItem> ToWatch { get; private set; } = [];
    public IReadOnlyList<LibraryCardItem> UpToDate { get; private set; } = [];
    public IReadOnlyList<LibraryCardItem> Watched { get; private set; } = [];

    public bool IsEmpty => Watching.Count == 0 && ToWatch.Count == 0 && UpToDate.Count == 0 && Watched.Count == 0;

    /// <summary>
    /// <c>/Library?section=watched</c> shows one section on its own, with its
    /// own heading and a link back: where a phone's "See all N" goes, since
    /// the full page shows that section there as a single scrolling row.
    /// Anything that is not a section's slug gives the full library.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Section { get; set; }

    /// <summary>The four sections in page order, empty ones included.</summary>
    public IReadOnlyList<LibrarySectionView> Sections =>
    [
        new(LibrarySection.Watching, "watching", "Watching", Watching),
        new(LibrarySection.ToWatch, "to-watch", "To Watch", ToWatch),
        new(LibrarySection.UpToDate, "up-to-date", "Up to Date", UpToDate),
        new(LibrarySection.Watched, "watched", "Watched", Watched)
    ];

    /// <summary>The section asked for with <see cref="Section"/>; null for the full library.</summary>
    public LibrarySectionView? SelectedSection =>
        string.IsNullOrWhiteSpace(Section)
            ? null
            : Sections.FirstOrDefault(s => string.Equals(s.Slug, Section.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Route values that bring a POST back to the view it came from.</summary>
    private object? BackToView => SelectedSection is { } selected ? new { section = selected.Slug } : null;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to view your library.");
            return Page();
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            // Repositories here all share the request's scoped AppDbContext (unlike
            // the TVDB/OMDb snapshot stores, which use IDbContextFactory) — EF Core
            // forbids concurrent operations on one context, so these stay sequential.
            var trackedSeries = await trackedSeriesRepository.GetByUserAsync(userId, cancellationToken);
            var likedSeries = await likeRepository.GetLikesAsync(userId, LikeTargetType.Series, cancellationToken);
            var watchedMovies = await movieWatchRepository.GetWatchedMoviesAsync(userId, cancellationToken);
            var watchlistMovies = await trackedMovieRepository.GetByUserAsync(userId, cancellationToken);

            var likedSeriesIds = likedSeries.Select(l => l.TargetTvdbId).ToHashSet();

            var watching = new List<LibraryCardItem>();
            var upToDate = new List<LibraryCardItem>();
            var watched = new List<LibraryCardItem>();

            await ClassifyTrackedSeriesAsync(userId, trackedSeries, likedSeriesIds, watching, upToDate, watched, cancellationToken);
            await AddWatchedMoviesAsync(watchedMovies, watched, cancellationToken);
            ToWatch = await BuildWatchlistAsync(watchlistMovies, cancellationToken);

            Watching = watching.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            UpToDate = upToDate.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Watched = watched.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load library for external user id {ExternalUserId}.", currentUserService.ExternalUserId);
            this.SetErrorToast("Could not load your library right now.");
            return Page();
        }
    }

    private async Task ClassifyTrackedSeriesAsync(
        Guid userId,
        IReadOnlyList<TrackedSeries> trackedSeries,
        IReadOnlySet<int> likedSeriesIds,
        List<LibraryCardItem> watching,
        List<LibraryCardItem> upToDate,
        List<LibraryCardItem> watched,
        CancellationToken cancellationToken)
    {
        if (trackedSeries.Count == 0)
            return;

        var aggregates = (await Task.WhenAll(
                trackedSeries.Select(s => theTvDbService.TryGetSeriesAggregateAsync(s.TvdbId, logger, nameof(LibraryModel), cancellationToken))))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        var seriesIds = aggregates.Select(a => a.TvdbId).ToList();
        var watchedEpisodeIds = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken);

        foreach (var aggregate in aggregates)
        {
            var progress = watchProgressService.BuildProgress(aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedEpisodeIds);

            // TheTVDB's own status text — "Ended" means no more episodes are coming.
            var hasEnded = aggregate.Status?.Name?.Equals("Ended", StringComparison.OrdinalIgnoreCase) == true;

            var item = new LibraryCardItem(
                SearchResultType.Series,
                aggregate.TvdbId,
                aggregate.Name,
                aggregate.ImageUrl,
                aggregate.FirstAired,
                progress.WatchedReleasedCount,
                progress.ReleasedCount,
                likedSeriesIds.Contains(aggregate.TvdbId),
                Caption: null);

            // Up to date is about regular episodes only: unwatched specials
            // never keep a series under Watching (see WatchProgressCalculator).
            if (!progress.IsUpToDate)
                watching.Add(item with { ProgressText = progress.CurrentSeason?.Label });
            else if (!hasEnded)
                upToDate.Add(item);
            else
                watched.Add(item);
        }
    }

    private async Task AddWatchedMoviesAsync(
        IReadOnlyList<MovieWatch> watchedMovies,
        List<LibraryCardItem> watched,
        CancellationToken cancellationToken)
    {
        if (watchedMovies.Count == 0)
            return;

        var aggregates = await Task.WhenAll(
            watchedMovies.Select(w => theTvDbService.TryGetMovieAggregateAsync(w.MovieTvdbId, logger, nameof(LibraryModel), cancellationToken)));

        for (var i = 0; i < watchedMovies.Count; i++)
        {
            if (aggregates[i] is not { } movie)
                continue;

            watched.Add(new LibraryCardItem(
                SearchResultType.Movie,
                movie.TvdbId,
                movie.Name,
                movie.ImageUrl,
                movie.ReleaseDate,
                WatchedEpisodes: 0,
                ReleasedEpisodes: 0,
                IsLiked: false,
                Caption: $"watched {DisplayDate.Short(watchedMovies[i].WatchedUtc, Today)}"));
        }
    }

    /// <summary>
    /// Keeps the watchlist's own order (most recently added first). A movie whose
    /// aggregate can't be loaded right now still gets a card, from the title
    /// stored when it was added — it shouldn't vanish from the list because
    /// TheTVDB had a bad moment.
    /// </summary>
    private async Task<IReadOnlyList<LibraryCardItem>> BuildWatchlistAsync(
        IReadOnlyList<TrackedMovie> watchlistMovies,
        CancellationToken cancellationToken)
    {
        if (watchlistMovies.Count == 0)
            return [];

        var aggregates = await Task.WhenAll(
            watchlistMovies.Select(m => theTvDbService.TryGetMovieAggregateAsync(m.MovieTvdbId, logger, nameof(LibraryModel), cancellationToken)));

        return watchlistMovies
            .Select((tracked, i) => new LibraryCardItem(
                SearchResultType.Movie,
                tracked.MovieTvdbId,
                aggregates[i]?.Name ?? tracked.Name,
                aggregates[i]?.ImageUrl,
                aggregates[i]?.ReleaseDate,
                WatchedEpisodes: 0,
                ReleasedEpisodes: 0,
                IsLiked: false,
                Caption: null))
            .ToList();
    }

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync(int id, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
        {
            this.SetErrorToast("You need to be signed in to like a series.");
            return RedirectToPage(BackToView);
        }

        try
        {
            await likeRepository.ToggleAsync(userId, LikeTargetType.Series, id, id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage(BackToView);
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to modify your library.");
            return await OnGetAsync(cancellationToken);
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

            await trackedSeriesRepository.RemoveAsync(userId, id, cancellationToken);
            return RedirectToPage(BackToView);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed removing tracked series {TrackedSeriesId} for external user id {ExternalUserId}.", id, currentUserService.ExternalUserId);
            this.SetErrorToast("Could not remove the series right now.");
            return await OnGetAsync(cancellationToken);
        }
    }
}
