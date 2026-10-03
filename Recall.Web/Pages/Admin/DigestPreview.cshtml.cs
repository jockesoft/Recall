using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Admin;

/// <summary>
/// Shows what a weekly digest would say, without sending or recording
/// anything. For an admin, of their own digest; in the Development
/// environment, of any user's (by email address), so a digest can be checked
/// against another library. Outside Development a digest is someone's library
/// and watch history, and an admin has no more business reading another
/// user's than anyone else.
/// </summary>
[Authorize(Roles = Roles.Admin)]
public sealed class DigestPreviewModel(
    ICurrentUserService currentUser,
    IAppUserRepository userRepository,
    IDigestComposer composer,
    IOptions<SiteOptions> siteOptions,
    IOptions<DigestOptions> digestOptions,
    IHostEnvironment environment,
    TimeProvider timeProvider) : PageModel
{
    /// <summary>Whose digest to preview, by email address. Only honoured where <see cref="CanChooseUser"/>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Email { get; set; }

    /// <summary>The date to build the digest for; today when empty.</summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? AsOf { get; set; }

    /// <summary>True in the Development environment only.</summary>
    public bool CanChooseUser => environment.IsDevelopment();

    public bool DigestEnabled => digestOptions.Value.Enabled;

    public DateOnly Today => AirDate.Today(timeProvider);

    /// <summary>
    /// The moment the preview is built for: now, or the chosen date at the
    /// hour the digest goes out (Digest:HourUtc), which is when its windows are judged.
    /// </summary>
    public DateTime AsOfMoment => AsOf is { } date
        ? date.ToDateTime(new TimeOnly(digestOptions.Value.HourUtc, 0), DateTimeKind.Utc)
        : AirDate.Now(timeProvider);

    /// <summary>The user whose digest is shown; null when there is nobody to show.</summary>
    public AppUserEntity? Subject { get; private set; }

    public ComposedDigest? Digest { get; private set; }

    /// <summary>Why there is no preview: another user's digest was asked for where that is not allowed, or nobody has that address.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The page-with-a-button unsubscribe link of this digest, for checking that page.</summary>
    public string? UnsubscribeUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } ownId)
            return RedirectToPage("/Account/Login");

        var own = await userRepository.GetByIdAsync(ownId, cancellationToken);
        if (own is null)
            return RedirectToPage("/Account/Login");

        var subject = own;
        var asked = Email?.Trim();

        if (!string.IsNullOrEmpty(asked) && !string.Equals(asked, own.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (!CanChooseUser)
            {
                Refusal = "Here you can only preview your own digest.";
                return Page();
            }

            var other = await userRepository.GetByEmailAsync(asked, cancellationToken);
            if (other is null)
            {
                Refusal = "No user has that email address.";
                return Page();
            }

            subject = other;
        }

        Subject = subject;

        // Without Site:BaseUrl (it has no default) the links point back at this request's own address.
        var baseUrl = siteOptions.Value.NormalizedBaseUrl ?? $"{Request.Scheme}://{Request.Host}";

        Digest = await composer.ComposeAsync(subject.Id, subject.Username, AsOfMoment, baseUrl, null, cancellationToken);
        UnsubscribeUrl = Digest.OneClickUnsubscribeUrl.Replace("/Digest/OneClick?", "/Digest/Unsubscribe?", StringComparison.Ordinal);

        return Page();
    }
}
