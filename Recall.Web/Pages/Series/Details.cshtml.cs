using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Models;

namespace Recall.Web.Pages.Series;

public sealed class DetailsModel : PageModel
{
    private readonly ITheTvDbService _theTvDbService;
    private readonly ILogger<DetailsModel> _logger;

    public DetailsModel(
        ITheTvDbService theTvDbService,
        ILogger<DetailsModel> logger)
    {
        _theTvDbService = theTvDbService;
        _logger = logger;
    }

    public TvSeriesDetails? Series { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            return NotFound();

        try
        {
            Series = await _theTvDbService.GetSeriesByIdAsync(id, cancellationToken);
            if (Series is null)
                return NotFound();

            return Page();
        }
        catch (TheTvDbApiException ex)
        {
            _logger.LogWarning(ex, "TheTVDB API error while loading details for id {SeriesId}.", id);
            ErrorMessage = "Could not fetch series details from TheTVDB right now. Please try again shortly.";
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while loading details for id {SeriesId}.", id);
            ErrorMessage = "An unexpected error occurred. Please try again.";
            return Page();
        }
    }
}