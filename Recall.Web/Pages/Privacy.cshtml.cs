using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Retention;

namespace Recall.Web.Pages;

/// <summary>
/// The privacy text states periods and names that live in code and
/// configuration. They are read from there, so the page keeps telling the
/// truth when a setting changes.
/// </summary>
public sealed class PrivacyModel(
    IOptions<RetentionOptions> retentionOptions,
    IOptions<LoginTokenOptions> loginOptions,
    IConfiguration configuration) : PageModel
{
    /// <summary>
    /// How many days of log files are kept: the file sink's
    /// <c>retainedFileCountLimit</c> when it rolls daily. Null when the logging
    /// configuration doesn't say, and the page then leaves the sentence out.
    /// </summary>
    public int? LogRetentionDays
    {
        get
        {
            var fileSink = configuration.GetSection("Serilog:WriteTo").GetChildren()
                .FirstOrDefault(sink => string.Equals(sink["Name"], "File", StringComparison.OrdinalIgnoreCase));

            return fileSink is not null
                   && string.Equals(fileSink["Args:rollingInterval"], "Day", StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(fileSink["Args:retainedFileCountLimit"], out var days)
                   && days > 0
                ? days
                : null;
        }
    }

    public RetentionOptions Retention => retentionOptions.Value;

    /// <summary>How long an emailed sign-in link works.</summary>
    public int SignInLinkMinutes => loginOptions.Value.TokenLifetimeMinutes;

    public string SignInCookieName => InfrastructureServiceCollectionExtensions.SignInCookieName;

    public int SignInCookieDays => InfrastructureServiceCollectionExtensions.SignInCookieDays;

    /// <summary>The key the cookie notice writes to the browser's local storage when it is dismissed.</summary>
    public const string CookieNoticeStorageKey = "recall.cookie-notice-dismissed";

    /// <summary>
    /// "deleted 30 days after …" for a retention period, or "kept" when that
    /// category's clean-up is switched off (a period of 0 or less).
    /// </summary>
    public static string After(int days, string what) =>
        days > 0
            ? $"deleted {days} {(days == 1 ? "day" : "days")} after {what}"
            : "kept (automatic clean-up is switched off for these)";

    public void OnGet()
    {
    }
}
