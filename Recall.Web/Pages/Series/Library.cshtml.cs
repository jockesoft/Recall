using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Web.Pages.Series;

public sealed class LibraryModel(
    ICurrentUserService currentUserService,
    IAppUserRepository appUserRepository,
    ITrackedSeriesRepository trackedSeriesRepository,
    ILogger<LibraryModel> logger)
    : PageModel
{
    public IReadOnlyList<TrackedSeries> Items { get; private set; } = Array.Empty<TrackedSeries>();
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            // If/when auth is wired, replace with Challenge() if desired.
            ErrorMessage = "You need to be signed in to view your library.";
            return Page();
        }

        try
        {
            var user = await appUserRepository.GetOrCreateByExternalIdAsync(
                currentUserService.ExternalUserId!,
                currentUserService.Email,
                currentUserService.DisplayName,
                cancellationToken);

            Items = await trackedSeriesRepository.GetByUserAsync(user.Id, cancellationToken);
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load library for external user id {ExternalUserId}.", currentUserService.ExternalUserId);
            ErrorMessage = "Could not load your library right now.";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            ErrorMessage = "You need to be signed in to modify your library.";
            return await OnGetAsync(cancellationToken);
        }

        try
        {
            var user = await appUserRepository.GetOrCreateByExternalIdAsync(
                currentUserService.ExternalUserId!,
                currentUserService.Email,
                currentUserService.DisplayName,
                cancellationToken);

            await trackedSeriesRepository.RemoveAsync(user.Id, id, cancellationToken);
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed removing tracked series {TrackedSeriesId} for external user id {ExternalUserId}.", id, currentUserService.ExternalUserId);
            ErrorMessage = "Could not remove the series right now.";
            return await OnGetAsync(cancellationToken);
        }
    }
}