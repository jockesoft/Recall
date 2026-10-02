using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages;

[Authorize]
public sealed class SearchModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    ITrackedMovieRepository trackedMovieRepository,
    IMovieWatchRepository movieWatchRepository,
    ILogger<SearchModel> logger)
    : PageModel
{
    /// <summary>Results shown before "Show more"; the rest are on the page, hidden.</summary>
    public const int InitialResultCount = 20;

    [BindProperty(SupportsGet = true)]
    [Display(Name = "Title")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Search text must be between 2 and 100 characters.")]
    public string? Query { get; set; }

    public IReadOnlyList<SearchResultItem> Results { get; private set; } = [];

    public string? ErrorMessage { get; private set; }

    public bool HasSearched => !string.IsNullOrWhiteSpace(Query);

    private IReadOnlySet<int> _trackedSeriesIds = new HashSet<int>();
    private IReadOnlySet<int> _watchlistMovieIds = new HashSet<int>();
    private IReadOnlySet<int> _watchedMovieIds = new HashSet<int>();

    /// <summary>
    /// "Watched" for a movie the user has watched, "In library" for a series
    /// they track or a movie on their watchlist, null for anything else.
    /// </summary>
    public string? LibraryBadge(SearchResultItem item) => item.Type switch
    {
        SearchResultType.Movie when _watchedMovieIds.Contains(item.TvdbId) => "Watched",
        SearchResultType.Movie when _watchlistMovieIds.Contains(item.TvdbId) => "In library",
        SearchResultType.Series when _trackedSeriesIds.Contains(item.TvdbId) => "In library",
        _ => null
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!HasSearched)
            return;

        if (!ModelState.IsValid)
            return;

        try
        {
            Results = await theTvDbService.SearchAsync(Query!, cancellationToken);
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while searching for query '{Query}'.", Query);
            ErrorMessage = "Could not fetch data from TheTVDB right now. Please try again shortly.";
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while searching for query '{Query}'.", Query);
            ErrorMessage = "An unexpected error occurred. Please try again.";
            return;
        }

        if (Results.Count > 0)
            await LoadLibraryStateAsync(cancellationToken);
    }

    /// <summary>
    /// What the user already has, for the badges. Best effort: the results are
    /// still worth showing without them. Sequential, since the three
    /// repositories share the request's DbContext.
    /// </summary>
    private async Task LoadLibraryStateAsync(CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
            return;

        try
        {
            _trackedSeriesIds = (await trackedSeriesRepository.GetByUserAsync(userId, cancellationToken))
                .Select(s => s.TvdbId).ToHashSet();
            _watchlistMovieIds = (await trackedMovieRepository.GetByUserAsync(userId, cancellationToken))
                .Select(m => m.MovieTvdbId).ToHashSet();
            _watchedMovieIds = (await movieWatchRepository.GetWatchedMoviesAsync(userId, cancellationToken))
                .Select(m => m.MovieTvdbId).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load library state for the search results.");
        }
    }
}
