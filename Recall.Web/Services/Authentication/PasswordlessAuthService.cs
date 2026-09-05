//-----------------------------------------------------------------------
// <copyright file="PasswordlessAuthService.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;

namespace Recall.Web.Services.Authentication;

public sealed class PasswordlessAuthService(
    IAppUserRepository userRepository,
    ILoginTokenRepository tokenRepository,
    IMailService mailService,
    ILoginAbuseGuard abuseGuard,
    IOptions<LoginTokenOptions> options,
    ILogger<PasswordlessAuthService> logger) : IPasswordlessAuthService
{
    /// <summary>256 bits of entropy — the raw token that goes in the email link.</summary>
    private const int TokenByteLength = 32;

    private readonly LoginTokenOptions _options = options.Value;

    public async Task RequestLoginAsync(
        string email,
        Func<string, string> loginLinkFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(loginLinkFactory);

        var normalizedEmail = email.Trim().ToLowerInvariant();

        // Registration allowlist: only enforced while non-empty. Once
        // registration is open (the intended end state) this is a no-op and
        // the checks below carry the abuse-prevention load instead.
        if (!IsEmailAllowed(normalizedEmail))
        {
            logger.LogWarning(
                "Passwordless sign-in request rejected: {Email} is not on the allowlist.", normalizedEmail);
            return;
        }

        // Per-address daily cap + site-wide hourly cap, both in-memory and
        // both checked before touching the database — a throttled request
        // never provisions an account or queues mail. Complements (doesn't
        // replace) the per-IP [EnableRateLimiting] policy on the page and the
        // resend cooldown below.
        if (!abuseGuard.TryAcquire(normalizedEmail))
        {
            logger.LogWarning(
                "Passwordless sign-in request throttled for {Email} (per-address or site-wide cap reached).",
                normalizedEmail);
            return;
        }

        var user = await userRepository.GetOrCreateByEmailAsync(normalizedEmail, cancellationToken);

        var nowUtc = DateTime.UtcNow;

        // Resend cooldown: if a link is already outstanding and was issued
        // moments ago, don't send another. Stops the form being used to bomb an
        // inbox regardless of how many requests come in.
        if (_options.ResendCooldownSeconds > 0)
        {
            var mostRecent = await tokenRepository.GetMostRecentActiveForUserAsync(user.Id, nowUtc, cancellationToken);
            if (mostRecent is not null &&
                mostRecent.CreatedUtc > nowUtc.AddSeconds(-_options.ResendCooldownSeconds))
            {
                logger.LogInformation(
                    "Passwordless sign-in request for user {UserId} ignored: within the {Cooldown}s resend cooldown.",
                    user.Id, _options.ResendCooldownSeconds);
                return;
            }
        }

        if (_options.InvalidatePreviousTokens)
            await tokenRepository.InvalidateActiveForUserAsync(user.Id, cancellationToken);

        var rawToken = GenerateRawToken();
#if DEBUG
        logger.LogDebug(rawToken);
#endif
        await tokenRepository.AddAsync(
            new LoginToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = Hash(rawToken),
                ExpiresUtc = nowUtc.AddMinutes(_options.TokenLifetimeMinutes)
            },
            cancellationToken);

        var link = loginLinkFactory(rawToken);

        await mailService.QueueEmailAsync(
            user.Email,
            "Your Recall sign-in link",
            BuildTextBody(link, _options.TokenLifetimeMinutes),
            BuildHtmlBody(link, _options.TokenLifetimeMinutes),
            MailService.NormalPriority,
            cancellationToken);

        logger.LogInformation(
            "Passwordless sign-in requested for user {UserId}; link valid for {Minutes} minute(s).",
            user.Id, _options.TokenLifetimeMinutes);
    }

    public async Task<LoginRedemptionResult> RedeemAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return LoginRedemptionResult.Invalid();

        var active = await tokenRepository.GetActiveByHashAsync(Hash(token), DateTime.UtcNow, cancellationToken);
        if (active is null)
        {
            logger.LogWarning("Passwordless sign-in failed: token unknown, expired, or already used.");
            return LoginRedemptionResult.Invalid();
        }

        // Consume first so a double-submit of the same link can't sign in twice.
        await tokenRepository.MarkConsumedAsync(active.Id, cancellationToken);

        var user = await userRepository.GetByIdAsync(active.UserId, cancellationToken);
        if (user is null)
        {
            logger.LogWarning("Passwordless sign-in failed: user {UserId} for token no longer exists.", active.UserId);
            return LoginRedemptionResult.Invalid();
        }

        logger.LogInformation("Passwordless sign-in succeeded for user {UserId} (role {Role}).", user.Id, user.Role);
        return LoginRedemptionResult.ForUser(user.Id, user.Email, user.Username, user.Role);
    }

    private bool IsEmailAllowed(string normalizedEmail)
    {
        if (_options.AllowedEmails is not { Length: > 0 } allowed)
            return true;

        foreach (var entry in allowed)
        {
            if (!string.IsNullOrWhiteSpace(entry) &&
                string.Equals(entry.Trim(), normalizedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GenerateRawToken() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    private static string Hash(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private static string BuildTextBody(string link, int lifetimeMinutes) =>
        $"""
         Sign in to Recall

         Open the link below to finish signing in. It works once and expires in {lifetimeMinutes} minutes.

         {link}

         If you didn't ask to sign in, you can ignore this email — nothing will happen.

         Recall · Track your shows · Never miss an episode
         """;

    /// <summary>
    /// A self-contained, table-based HTML email in the recall.nu palette
    /// (dark "broadcast slate" ink, paper card, amber signal). All styling is
    /// inline for mail-client compatibility.
    /// </summary>
    private static string BuildHtmlBody(string link, int lifetimeMinutes)
    {
        var href = WebUtility.HtmlEncode(link);
        const string sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
        const string mono = "'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace";

        return $"""
                <!DOCTYPE html>
                <html lang="en" xmlns="http://www.w3.org/1999/xhtml">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta name="color-scheme" content="light">
                <meta name="supported-color-schemes" content="light">
                <title>Your Recall sign-in link</title>
                </head>
                <body style="margin:0;padding:0;background:#151b24;">
                <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;">Your sign-in link — works once, expires in {lifetimeMinutes} minutes.</div>
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#151b24;">
                  <tr>
                    <td align="center" style="padding:32px 16px;">
                      <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0" style="width:600px;max-width:600px;">
                        <tr>
                          <td style="background:#1f2733;border-radius:12px 12px 0 0;padding:22px 32px;border-bottom:2px solid #e8a33d;">
                            <span style="display:inline-block;width:8px;height:8px;border-radius:50%;background:#e8a33d;vertical-align:middle;"></span>
                            <span style="font-family:{sans};font-weight:700;letter-spacing:.22em;font-size:15px;color:#f1ece2;text-transform:uppercase;vertical-align:middle;padding-left:10px;">Recall</span>
                          </td>
                        </tr>
                        <tr>
                          <td style="background:#f1ece2;padding:34px 32px;font-family:{sans};color:#2c2a24;">
                            <h1 style="margin:0 0 12px;font-size:22px;line-height:1.25;color:#2c2a24;">Sign in to Recall</h1>
                            <p style="margin:0 0 26px;font-size:15px;line-height:1.6;color:#4a4638;">
                              Tap the button to finish signing in. This link works once and expires in {lifetimeMinutes} minutes.
                            </p>
                            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 26px;">
                              <tr>
                                <td align="center" bgcolor="#e8a33d" style="border-radius:6px;">
                                  <a href="{href}" target="_blank" style="display:inline-block;padding:14px 30px;font-family:{sans};font-size:15px;font-weight:700;line-height:1;color:#2a1a05;text-decoration:none;border-radius:6px;">Sign in to Recall</a>
                                </td>
                              </tr>
                            </table>
                            <p style="margin:0 0 8px;font-size:13px;line-height:1.5;color:#766c59;">Button not working? Paste this address into your browser:</p>
                            <p style="margin:0 0 26px;font-size:12px;line-height:1.5;word-break:break-all;font-family:{mono};color:#4a4638;">{href}</p>
                            <hr style="border:0;border-top:1px solid rgba(21,27,36,.14);margin:0 0 16px;">
                            <p style="margin:0;font-size:12px;line-height:1.5;color:#766c59;">If you didn't ask to sign in, you can ignore this email — nothing will happen.</p>
                          </td>
                        </tr>
                        <tr>
                          <td style="background:#1f2733;border-radius:0 0 12px 12px;padding:18px 32px;font-family:{sans};font-size:11px;letter-spacing:.05em;color:rgba(241,236,226,.5);">
                            Recall &middot; Track your shows &middot; Never miss an episode
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                </table>
                </body>
                </html>
                """;
    }
}
