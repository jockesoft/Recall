using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Import;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Account;

/// <summary>One figure of the import summary: an outcome and how many rows ended there.</summary>
public sealed record ImportOutcomeCount(ImportStatusDisplay Display, int Count);

[Authorize]
public sealed class ImportWatchlistModel(
    ICurrentUserService currentUser,
    IWatchlistImportRepository importRepository,
    IWatchlistImportService importService,
    TimeProvider timeProvider,
    ILogger<ImportWatchlistModel> logger) : PageModel
{
    /// <summary>Today's date in UTC, for the date format of "when was this imported".</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    /// <summary>Generous headroom over a 600-row export — this isn't meant for huge files.</summary>
    private const long MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>How often the page reloads itself while an import is still being matched.</summary>
    public const int RefreshSeconds = 15;

    public WatchlistImportJob? LatestJob { get; private set; }

    /// <summary>True while rows are still waiting to be matched: the page shows progress and refreshes itself.</summary>
    public bool IsProcessing => LatestJob is { Status: WatchlistImportJobStatus.Processing };

    /// <summary>
    /// How many rows ended in each outcome, in the order the summary shows them
    /// (every outcome, including the ones at zero). Counted from the rows, since
    /// the job's own counters fold "Unsupported" into "Already had it".
    /// </summary>
    public IReadOnlyList<ImportOutcomeCount> Summary =>
        LatestJob is null
            ? []
            : ImportStatusDisplay.Outcomes
                .Select(status => new ImportOutcomeCount(
                    ImportStatusDisplay.For(status),
                    LatestJob.Items.Count(item => item.Status == status)))
                .ToList();

    /// <summary>"Movie · rated 8", or just the type for a row without a rating.</summary>
    public static string MetaLine(WatchlistImportItem item) =>
        item.YourRating is { } rating ? $"{item.TitleType} · rated {rating}" : item.TitleType;

    /// <summary>The details page of the title a row was matched to; null while it has no match.</summary>
    public static string? DetailsPage(WatchlistImportItem item) =>
        item.ResolvedTvdbId is null
            ? null
            : string.Equals(item.TitleType, "Movie", StringComparison.OrdinalIgnoreCase)
                ? "/Movies/Details"
                : "/Series/Details";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        try
        {
            LatestJob = await importRepository.GetLatestJobForUserAsync(userId, includeItems: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the latest watchlist import job.");
        }
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return RedirectToPage();

        if (file is null || file.Length == 0)
        {
            this.SetErrorToast("Choose a CSV file to import.");
            return RedirectToPage();
        }

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            this.SetErrorToast("That doesn't look like a CSV file.");
            return RedirectToPage();
        }

        if (file.Length > MaxUploadBytes)
        {
            this.SetErrorToast("That file is too large — IMDb exports are usually well under 2 MB.");
            return RedirectToPage();
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var job = await importService.StartImportAsync(userId, stream, file.FileName, cancellationToken);
            this.SetSuccessToast($"Importing {job.TotalCount} {(job.TotalCount == 1 ? "row" : "rows")} from {file.FileName}. Matching runs in the background.");
        }
        catch (InvalidOperationException ex)
        {
            this.SetErrorToast(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to start a watchlist import for user {UserId}.", userId);
            this.SetErrorToast("Could not read that file. Make sure it's an unmodified IMDb export.");
        }

        return RedirectToPage();
    }
}
