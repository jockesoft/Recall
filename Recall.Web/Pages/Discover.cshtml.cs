using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.Discover;

namespace Recall.Web.Pages;

/// <summary>
/// Discover: rows of series and movies the user does not have yet, picked
/// from the lists the daily job fetches (<see cref="DiscoverBuilder"/>). A
/// view reads the database and the caches only, never TheTVDB.
/// </summary>
[Authorize]
public sealed class DiscoverModel(
    IDiscoverService discoverService,
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
}
