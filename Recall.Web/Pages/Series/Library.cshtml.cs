using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Web.Pages.Series;

public sealed class LibraryModel(
    ICurrentUserService currentUserService,
    ITrackedSeriesRepository trackedSeriesRepository,
    ILogger<LibraryModel> logger)
    : PageModel
{
    public IReadOnlyList<TrackedSeries> Items { get; private set; } = Array.Empty<TrackedSeries>();

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
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load library for external user id {ExternalUserId}.", currentUserService.ExternalUserId);
            this.SetErrorToast("Could not load your library right now.");
            return Page();
        }
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