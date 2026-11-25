using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.Discover;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages;

/// <summary>
/// Discover: rows of series and movies the user does not have yet, picked
/// from the lists the daily job fetches (<see cref="DiscoverBuilder"/>). A
/// view reads the database and the caches only, never TheTVDB. On each poster,
/// "+" adds a series to the library (a bookmark adds a movie to the watchlist)
/// and "×" hides the title ("Not interested"); both toasts have an Undo, and
/// every POST returns to the card's place in its row (<see cref="ListPosition"/>).
/// </summary>
[Authorize]
public sealed class DiscoverModel(
    IDiscoverService discoverService,
    IDiscoverUserRepository discoverUserRepository,
    ISeriesLibraryService seriesLibraryService,
    IMovieTrackingService movieTrackingService,
    ICurrentUserService currentUserService,
    ILogger<DiscoverModel> logger)
    : PageModel
{
    /// <summary>The rows, or none (with <see cref="DiscoverPage.HasLists"/> false) before the job's first run.</summary>
    public DiscoverPage Suggestions { get; private set; } = DiscoverPage.NotYetFetched;

    /// <summary>The page could not be built: it says so instead of showing rows.</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>How many posters of the first row load at once; the rest, and every later row, load lazily.</summary>
    public const int EagerPosters = 8;

    /// <summary>
    /// The card a POST came from ("discover-new-5"): the page comes back to
    /// that place, now holding the next title. Anything that is not a Discover
    /// card is dropped.
    /// </summary>
    [BindProperty]
    public string? Anchor { get; set; }

    /// <summary>The card was the last of its row: when it leaves, the page returns to the new last one.</summary>
    [BindProperty(Name = "last")]
    public bool AnchorIsLast { get; set; }

    /// <summary>Every name a row's cards can have: the fixed rows' and one per genre.</summary>
    public static IReadOnlyCollection<string> RowPrefixes { get; } =
        new[] { DiscoverBuilder.NewSlug, DiscoverBuilder.PopularSlug, DiscoverBuilder.FavouritesSlug, DiscoverBuilder.MoviesSlug }
            .Concat(DiscoverGenres.Ids.Keys.Select(DiscoverBuilder.GenreSlug))
            .Select(slug => "discover-" + slug)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A card's id: its row and its place, 1-based ("discover-new-5").</summary>
    public static string CardId(DiscoverRow row, int position) => ListPosition.Id(RowPrefix(row), position);

    /// <summary>"discover-new": what a row's cards are named by, and the row's section id.</summary>
    public static string RowPrefix(DiscoverRow row) => "discover-" + row.Slug;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
            return Challenge();

        try
        {
            Suggestions = await discoverService.GetPageAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to build the Discover page for user {UserId}.", userId);
            LoadFailed = true;
            Response.StatusCode = StatusCodes.Status500InternalServerError;
        }

        return Page();
    }

    /// <summary>
    /// "+": a series into the library (through the same service as Series
    /// Details, which fetches its aggregate if nobody has opened it yet), or a
    /// movie onto the watchlist (with its name from the list, so no lookup).
    /// Only a title the user's page offers today is added.
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(string? kind, int id, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
            return Challenge();

        var left = false;

        try
        {
            var title = await OfferedAsync(userId, kind, id, cancellationToken);
            if (title is null)
            {
                this.SetErrorToast(NotOffered);
                return BackToCard(left);
            }

            left = title.Kind == DiscoverKind.Movie
                ? await AddMovieAsync(userId, title, cancellationToken)
                : await AddSeriesAsync(userId, title, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed adding {Kind} {TvdbId} from Discover.", kind, id);
            this.SetErrorToast("Could not add that right now.");
        }

        return BackToCard(left);
    }

    /// <summary>"×": hides the title from every row of the user's Discover. The toast offers Undo.</summary>
    public async Task<IActionResult> OnPostHideAsync(string? kind, int id, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
            return Challenge();

        var left = false;

        try
        {
            var title = await OfferedAsync(userId, kind, id, cancellationToken);
            if (title is null)
            {
                this.SetErrorToast(NotOffered);
                return BackToCard(left);
            }

            // False is a second press: hidden either way, and the Undo still shows it again.
            await discoverUserRepository.HideAsync(userId, title.Kind, title.TvdbId, cancellationToken);
            this.SetDiscoverToast(HiddenMessage, new DiscoverUndo(DiscoverUndoAction.Hide, title.Kind, title.TvdbId, Anchor));
            left = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed hiding {Kind} {TvdbId} from Discover.", kind, id);
            this.SetErrorToast("Could not hide that right now.");
        }

        return BackToCard(left);
    }

    /// <summary>
    /// The Undo of an add (the series out of the library, the movie off the
    /// watchlist) or of "Not interested" (the title shown again). The card is
    /// back at its place, so the page returns there.
    /// </summary>
    public async Task<IActionResult> OnPostUndoAsync(string? action, string? kind, int id, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not { } userId)
            return Challenge();

        if (!TryParse<DiscoverUndoAction>(action, out var undo) || !TryParse<DiscoverKind>(kind, out var titleKind) || id <= 0)
        {
            this.SetInfoToast(NothingToUndo);
            return BackToCard(leftTheList: false);
        }

        try
        {
            var name = (await discoverService.FindAsync(userId, titleKind, id, cancellationToken))?.Name ?? "It";

            var undone = undo switch
            {
                DiscoverUndoAction.Hide => await discoverUserRepository.UnhideAsync(userId, titleKind, id, cancellationToken),
                _ when titleKind == DiscoverKind.Movie => await movieTrackingService.RemoveFromWatchlistAsync(userId, id, cancellationToken),
                _ => await seriesLibraryService.RemoveAsync(userId, id, cancellationToken)
            };

            if (!undone)
                this.SetInfoToast(NothingToUndo);
            else if (undo == DiscoverUndoAction.Hide)
                this.SetInfoToast($"{name} is back in Discover.");
            else
                this.SetInfoToast(titleKind == DiscoverKind.Movie
                    ? $"Took {name} off your watchlist again."
                    : $"Took {name} out of your library again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed undoing {Action} of {Kind} {TvdbId} on Discover.", action, kind, id);
            this.SetErrorToast("Could not undo that right now.");
        }

        return BackToCard(leftTheList: false);
    }

    public const string HiddenMessage = "Hidden from Discover.";
    public const string NotOffered = "That title is no longer suggested. The page has been updated.";
    public const string NothingToUndo = "Nothing left to undo.";

    /// <summary>True when the series left the row (added, or already in the library).</summary>
    private async Task<bool> AddSeriesAsync(Guid userId, DiscoverTitle title, CancellationToken cancellationToken)
    {
        var result = await seriesLibraryService.AddAsync(userId, title.TvdbId, cancellationToken);
        var name = result.SeriesName ?? title.Name;

        switch (result.Outcome)
        {
            case AddSeriesOutcome.Added:
                this.SetDiscoverToast($"Added {name} to your library.", new DiscoverUndo(DiscoverUndoAction.Add, title.Kind, title.TvdbId, Anchor));
                return true;
            case AddSeriesOutcome.AlreadyInLibrary:
                this.SetInfoToast($"{name} is already in your library.");
                return true;
            default:
                this.SetErrorToast($"TheTVDB no longer has {name}, so it could not be added.");
                return false;
        }
    }

    /// <summary>True when the movie left the row (added, already on the watchlist, or already watched).</summary>
    private async Task<bool> AddMovieAsync(Guid userId, DiscoverTitle title, CancellationToken cancellationToken)
    {
        switch (await movieTrackingService.AddToWatchlistAsync(userId, title.TvdbId, knownName: title.Name, cancellationToken))
        {
            case MovieWatchlistOutcome.Added:
                this.SetDiscoverToast($"Added {title.Name} to your watchlist.", new DiscoverUndo(DiscoverUndoAction.Add, title.Kind, title.TvdbId, Anchor));
                return true;
            case MovieWatchlistOutcome.AlreadyOnWatchlist:
                this.SetInfoToast($"{title.Name} is already on your watchlist.");
                return true;
            case MovieWatchlistOutcome.AlreadyWatched:
                this.SetInfoToast($"You've already watched {title.Name}.");
                return true;
            default:
                this.SetErrorToast($"TheTVDB no longer has {title.Name}, so it could not be added.");
                return false;
        }
    }

    /// <summary>The title, when the user's page offers it today; null for anything else.</summary>
    private async Task<DiscoverTitle?> OfferedAsync(Guid userId, string? kind, int id, CancellationToken cancellationToken) =>
        TryParse<DiscoverKind>(kind, out var titleKind) && id > 0
            ? await discoverService.FindAsync(userId, titleKind, id, cancellationToken)
            : null;

    private static bool TryParse<T>(string? value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _);

    /// <summary>Back to the card's place; when the row's only card left, to the row.</summary>
    private RedirectToPageResult BackToCard(bool leftTheList)
    {
        var fragment = ListPosition.ReturnTo(Anchor, RowPrefixes, leftTheList, AnchorIsLast,
            emptyFallback: Anchor is { } anchor ? RowPrefixes.FirstOrDefault(p => anchor.StartsWith(p + "-", StringComparison.Ordinal)) : null);

        return RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: fragment);
    }
}
