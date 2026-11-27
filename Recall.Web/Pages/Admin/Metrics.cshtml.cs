using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Metrics;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Admin;

/// <summary>
/// The site metrics (/Admin/Metrics): the last 30 days, the 7- and 30-day
/// totals, and the sign-up funnel. The stored rows plus what has been counted
/// since the last flush, so the page is up to the minute.
/// </summary>
[Authorize(Roles = Roles.Admin)]
public sealed class MetricsModel(
    ISiteMetricRepository repository,
    SiteMetricsBuffer buffer,
    TimeProvider timeProvider) : PageModel
{
    public SiteMetricsReport Report { get; private set; } = null!;

    public DateOnly Today => AirDate.Today(timeProvider);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var today = Today;
        var stored = await repository.GetAsync(today.AddDays(-(SiteMetricsReport.LongWindow - 1)), today, cancellationToken);

        Report = SiteMetricsReport.Build(stored.Concat(buffer.Pending()), today);
    }

    /// <summary>A count with thousands separators: "1,204".</summary>
    public static string Count(long value) => value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    /// <summary>"42%" (rounded to a whole percent, "0.4%" below one), or a dash when there is nothing to compare with.</summary>
    public static string Percent(double? rate) => rate switch
    {
        null => "–",
        < 0.01 and > 0 => (rate.Value * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%",
        _ => Math.Round(rate.Value * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
    };
}
