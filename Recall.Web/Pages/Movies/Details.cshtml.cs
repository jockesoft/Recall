using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Pages.Movies;

/// <summary>
/// Public, anonymous-friendly movie details page — no library/like/rating actions
/// yet, those are added once movie tracking has its own data model.
/// </summary>
public sealed class DetailsModel(
    ITheTvDbService theTvDbService,
    ILogger<DetailsModel> logger)
    : PageModel
{
    public MovieAggregate? Movie { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (id <= 0) return NotFound();

        try
        {
            Movie = await theTvDbService.GetMovieAggregateByIdAsync(id, cancellationToken);
            return Movie is null ? NotFound() : Page();
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogWarning(ex, "TheTVDB API error while loading movie details for id {MovieId}.", id);
            this.SetErrorToast("Could not fetch movie details from TheTVDB right now.");
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while loading movie details for id {MovieId}.", id);
            this.SetErrorToast("An unexpected error occurred.");
            return Page();
        }
    }
}
