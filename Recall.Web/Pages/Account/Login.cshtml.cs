using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Services.Authentication;

namespace Recall.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(InfrastructureServiceCollectionExtensions.LoginEmailPolicy)]
public sealed class LoginModel(
    IPasswordlessAuthService authService,
    ITurnstileVerifier turnstileVerifier,
    IOptions<TurnstileOptions> turnstileOptions,
    IOptions<LoginTokenOptions> loginOptions,
    ILogger<LoginModel> logger) : PageModel
{
    /// <summary>Real users take more than this to type an email and submit. Bots that
    /// clone-and-POST the form usually don't.</summary>
    private static readonly TimeSpan MinimumHumanSubmitDelay = TimeSpan.FromSeconds(2);

    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(320, ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Honeypot. Hidden from users; automated form-fillers tend to populate any
    /// field named like this. A non-empty value means "bot" — we no-op.
    /// </summary>
    [BindProperty]
    public string? Website { get; set; }

    /// <summary>
    /// Timing trap: set to "now" when the form is rendered. A submission that
    /// arrives faster than a human could plausibly type an email address is
    /// treated the same as a tripped honeypot.
    /// </summary>
    [BindProperty]
    public string? FormRenderedAtUtc { get; set; }

    /// <summary>Token the Turnstile widget places on the form once solved.</summary>
    [BindProperty(Name = "cf-turnstile-response")]
    public string? TurnstileToken { get; set; }

    public bool TurnstileEnabled => turnstileOptions.Value.IsConfigured;

    public string? TurnstileSiteKey => turnstileOptions.Value.SiteKey;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>True once a link has been sent, so the view shows the "check your inbox" state.</summary>
    public bool LinkSent { get; private set; }

    /// <summary>How long an emailed link works, from configuration, so the page tells the truth about it.</summary>
    public int LinkLifetimeMinutes => loginOptions.Value.TokenLifetimeMinutes;

    /// <summary>The code as typed, kept when it did not work so it can be corrected.</summary>
    public string? Code { get; private set; }

    /// <summary>Why the code did not sign in; null before one is tried.</summary>
    public string? CodeError { get; private set; }

    /// <summary>Too many wrong codes since the last email: the field gives way to "request a new email".</summary>
    public bool CodeLockedOut { get; private set; }

    /// <summary>
    /// One answer for a wrong code, an expired one and an address without an
    /// account, so the form cannot be used to find out which addresses have one.
    /// </summary>
    public const string WrongCodeMessage = "That code didn't work. Check it, or request a new email if it has expired.";

    public const string TooManyCodesMessage = "Too many wrong codes. Request a new email to get a new link and code.";

    public void OnGet()
    {
        // Unix milliseconds rather than an ISO-8601 string: a plain integer
        // has no characters (":", "+") that need HTML-entity round-tripping
        // through the hidden input's rendered attribute value.
        FormRenderedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(Website) || !SubmittedLikeAHuman())
        {
            // Honeypot or timing trap tripped — look identical to a real
            // submission so the bot doesn't learn it was caught, but send
            // nothing.
            logger.LogWarning(
                "Login bot trap tripped from {RemoteIp} (honeypot: {Honeypot}).",
                HttpContext.Connection.RemoteIpAddress, !string.IsNullOrEmpty(Website));
            LinkSent = true;
            return Page();
        }

        if (!ModelState.IsValid)
            return Page();

        if (TurnstileEnabled)
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var passedChallenge = await turnstileVerifier.VerifyAsync(TurnstileToken, remoteIp, cancellationToken);
            if (!passedChallenge)
            {
                // A real person can just retry, so — unlike the honeypot/timing
                // trap above — this fails visibly instead of pretending to succeed.
                ModelState.AddModelError(string.Empty, "We couldn't verify that you're not a robot. Please try again.");
                return Page();
            }
        }

        var scheme = Request.Scheme;
        var returnUrl = SafeReturnUrl();

        await authService.RequestLoginAsync(
            Email,
            token => Url.Page(
                "/Account/Verify",
                pageHandler: null,
                values: new { token, returnUrl },
                protocol: scheme)!,
            cancellationToken);

        // Always report the same thing whether or not the account existed.
        LinkSent = true;
        return Page();
    }

    /// <summary>
    /// The code from the email, for where the link does not open: a Home Screen
    /// app on an iPhone has cookies of its own, and the link opens Safari. The
    /// form carries the address it was sent to, so it needs no cookie of ours
    /// beyond the antiforgery one. Rate limited per IP in its own bucket
    /// (<c>LoginEmailPartition</c>).
    /// </summary>
    public async Task<IActionResult> OnPostVerifyCodeAsync(string? code, CancellationToken cancellationToken)
    {
        // Back to "check your inbox", with the field, whatever happens below.
        LinkSent = true;
        Code = code;
        ModelState.Clear();

        var result = await authService.RedeemCodeAsync(Email, code, cancellationToken);

        switch (result.Status)
        {
            case LoginRedemptionStatus.Success:
                // As Pages/Account/Verify does for the link.
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    RecallPrincipal.Create(result.UserId, result.DisplayName, result.Email, result.Role),
                    new AuthenticationProperties { IsPersistent = true });
                return LocalRedirect(SafeReturnUrl());

            case LoginRedemptionStatus.TooManyAttempts:
                CodeLockedOut = true;
                CodeError = TooManyCodesMessage;
                return Page();

            default:
                CodeError = WrongCodeMessage;
                return Page();
        }
    }

    private bool SubmittedLikeAHuman()
    {
        if (!long.TryParse(FormRenderedAtUtc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var renderedAtMs))
        {
            return false; // Missing or malformed — the field a real page render always sets.
        }

        var renderedAt = DateTimeOffset.FromUnixTimeMilliseconds(renderedAtMs);
        return DateTimeOffset.UtcNow - renderedAt >= MinimumHumanSubmitDelay;
    }

    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/Dashboard";
}
