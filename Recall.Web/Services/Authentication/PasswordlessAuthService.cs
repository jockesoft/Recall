//-----------------------------------------------------------------------
// <copyright file="PasswordlessAuthService.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

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
            BuildEmailBody(link, _options.TokenLifetimeMinutes),
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

        logger.LogInformation("Passwordless sign-in succeeded for user {UserId}.", user.Id);
        return LoginRedemptionResult.ForUser(user.Id, user.Email, user.Username);
    }

    private static string GenerateRawToken() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    private static string Hash(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private static string BuildEmailBody(string link, int lifetimeMinutes) =>
        $"""
         Hi,

         Use the link below to sign in to Recall. It works once and expires in {lifetimeMinutes} minutes.

         {link}

         If you didn't ask to sign in, you can ignore this email.
         """;
}
