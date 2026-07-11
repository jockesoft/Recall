using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;
using Recall.Web.Pages;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Models;

namespace Recall.Web.Pages.Series;

public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ICurrentUserService currentUserService,
    IAppUserRepository appUserRepository,
    ITrackedSeriesRepository trackedSeriesRepository,
    ILogger<DetailsModel> logger)
    : PageModel
{
    public TvSeriesDetails? Series { get; private set; }
    public SeriesAggregate? Aggregate { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int? Season { get; set; }

    public bool IsTrackedByCurrentUser { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
        => await LoadPageAsync(id, cancellationToken);

    public async Task<IActionResult> OnPostToggleLibraryAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
        {
            this.SetErrorToast("You need to be signed in to manage your library.");
            return RedirectToPage(new { id, season = Season });
        }

        try
        {
            var user = await appUserRepository.GetOrCreateByExternalIdAsync(
                currentUserService.ExternalUserId!,
                currentUserService.Email,
                currentUserService.DisplayName,
                cancellationToken);

            var existing = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(user.Id, id, cancellationToken);

            if (existing is null)
            {
                var series = await theTvDbService.GetSeriesByIdAsync(id, cancellationToken);
                if (series is null) return NotFound();

                var tracked = TrackedSeriesMappings.FromTvDbDetails(user.Id, series);
                await trackedSeriesRepository.AddAsync(tracked, cancellationToken);
                this.SetSuccessToast("Series saved to your library.");
            }
            else
            {
                await trackedSeriesRepository.RemoveAsync(user.Id, existing.Id, cancellationToken);
                this.SetInfoToast("Series removed from your library.");
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in your library", StringComparison.OrdinalIgnoreCase))
        {
            this.SetInfoToast("Series is already in your library.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling library state for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your library right now.");
        }

        return RedirectToPage(new { id, season = Season });
    }

    private async Task<IActionResult> LoadPageAsync(int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(id, cancellationToken);
            if (Aggregate is null) return NotFound();

            Series = new TvSeriesDetails(
                Aggregate.TvdbId,
                Aggregate.Name,
                Aggregate.Slug,
                Aggregate.Overview,
                Aggregate.ImageUrl,
                Aggregate.FirstAired?.ToString("yyyy-MM-dd"),
                Aggregate.Score);

            if (!currentUserService.IsAuthenticated || string.IsNullOrWhiteSpace(currentUserService.ExternalUserId))
                return Page();

            var user = await appUserRepository.GetOrCreateByExternalIdAsync(
                currentUserService.ExternalUserId!,
                currentUserService.Email,
                currentUserService.DisplayName,
                cancellationToken);

            IsTrackedByCurrentUser = await trackedSeriesRepository.ExistsAsync(user.Id, id, cancellationToken);
            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading details for id {SeriesId}.", id);
            this.SetErrorToast("Could not fetch series details from TheTVDB right now.");
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while loading details for id {SeriesId}.", id);
            this.SetErrorToast("An unexpected error occurred.");
            return Page();
        }
    }

    public string GetSeasonName(int season)
    {
        if (season == 0) return "Specials";
        else return "Season " + season;
    }
}