using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Series;

public sealed class SearchModel(
    ITheTvDbService theTvDbService,
    ILogger<SearchModel> logger)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    [Display(Name = "Series title")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Search text must be between 2 and 100 characters.")]
    public string? Query { get; set; }

    public IReadOnlyList<TvSeriesSummary> Results { get; private set; } = [];

    public string? ErrorMessage { get; private set; }

    public bool HasSearched => !string.IsNullOrWhiteSpace(Query);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!HasSearched)
            return;

        if (!ModelState.IsValid)
            return;

        try
        {
            Results = await theTvDbService.SearchSeriesAsync(Query!, cancellationToken);
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while searching for query '{Query}'.", Query);
            ErrorMessage = "Could not fetch data from TheTVDB right now. Please try again shortly.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while searching for query '{Query}'.", Query);
            ErrorMessage = "An unexpected error occurred. Please try again.";
        }
    }
}