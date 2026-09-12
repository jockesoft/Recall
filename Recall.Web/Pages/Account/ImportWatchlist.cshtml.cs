using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Import;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class ImportWatchlistModel(
    ICurrentUserService currentUser,
    IWatchlistImportRepository importRepository,
    IWatchlistImportService importService,
    ILogger<ImportWatchlistModel> logger) : PageModel
{
    /// <summary>Generous headroom over a 600-row export — this isn't meant for huge files.</summary>
    private const long MaxUploadBytes = 2 * 1024 * 1024;

    public WatchlistImportJob? LatestJob { get; private set; }

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
            this.SetSuccessToast($"Importing {job.TotalCount} row(s) from {file.FileName} — this happens gradually in the background.");
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
