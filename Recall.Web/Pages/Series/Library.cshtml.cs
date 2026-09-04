using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;
using Microsoft.AspNetCore.Authorization;
namespace Recall.Web.Pages.Series;

[Authorize]
public sealed class LibraryModel(
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    IWatchProgressService watchProgressService,
    IEpisodeWatchRepository episodeWatchRepository,
    ILikeRepository likeRepository,
    ILogger<LibraryModel> logger)
    : PageModel
{
    public IReadOnlyList<TrackedSeries> Items { get; private set; } = Array.Empty<TrackedSeries>();

    /// <summary>TVDB ids of the tracked series the user has also hearted.</summary>
    public IReadOnlySet<int> LikedSeriesIds { get; private set; } = new HashSet<int>();

    /// <summary>
    /// Per-series watch progress over aired episodes, keyed by TVDB id. Drives
    /// the small progress bar on each poster. A series missing from the map (or
    /// with <c>Released == 0</c>) simply gets no bar.
    /// </summary>
    public IReadOnlyDictionary<int, SeriesProgress> ProgressByTvdbId { get; private set; }
        = new Dictionary<int, SeriesProgress>();

    public readonly record struct SeriesProgress(int WatchedReleased, int Released);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            // If/when auth is wired, replace with Challenge() if desired.
            this.SetErrorToast("You need to be signed in to view your library.");
            return Page();
        }

        try
        {
            var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
            Items = await trackedSeriesRepository.GetByUserAsync(userId, cancellationToken);
            ProgressByTvdbId = await BuildProgressAsync(userId, Items, cancellationToken);

            var likes = await likeRepository.GetLikesAsync(userId, LikeTargetType.Series, cancellationToken);
            LikedSeriesIds = likes.Select(l => l.TargetTvdbId).ToHashSet();

            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load library for external user id {ExternalUserId}.", currentUserService.ExternalUserId);
            this.SetErrorToast("Could not load your library right now.");
            return Page();
        }
    }

    private async Task<IReadOnlyDictionary<int, SeriesProgress>> BuildProgressAsync(
        Guid userId,
        IReadOnlyList<TrackedSeries> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return new Dictionary<int, SeriesProgress>();

        var seriesIds = items.Select(i => i.TvdbId).ToList();

        var watchedIdsTask = episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken);
        var episodeListsTask = Task.WhenAll(
            seriesIds.Select(id => TryGetOrderedEpisodesAsync(id, cancellationToken)));

        await Task.WhenAll(watchedIdsTask, episodeListsTask);
        var watchedIds = watchedIdsTask.Result;
        var episodeLists = episodeListsTask.Result;

        var result = new Dictionary<int, SeriesProgress>(seriesIds.Count);
        for (var i = 0; i < seriesIds.Count; i++)
        {
            var progress = watchProgressService.BuildProgress(seriesIds[i], episodeLists[i], watchedIds);
            result[seriesIds[i]] = new SeriesProgress(progress.WatchedReleasedCount, progress.ReleasedCount);
        }

        return result;
    }

    private async Task<IReadOnlyList<WatchableEpisode>> TryGetOrderedEpisodesAsync(
        int seriesTvdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await watchProgressService.GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load episodes for tracked series {SeriesId} (library progress bar).", seriesTvdbId);
            return [];
        }
    }

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync(int id, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
        {
            this.SetErrorToast("You need to be signed in to like a series.");
            return RedirectToPage();
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

        return RedirectToPage();
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
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed removing tracked series {TrackedSeriesId} for external user id {ExternalUserId}.", id, currentUserService.ExternalUserId);
            this.SetErrorToast("Could not remove the series right now.");
            return await OnGetAsync(cancellationToken);
        }
    }
}