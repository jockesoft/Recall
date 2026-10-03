using Microsoft.Extensions.Options;
using Recall.Web.Domain.Internal;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Digest;

/// <summary>What one run of the digest job did.</summary>
public sealed record DigestRunResult(DateOnly? Period, int Queued, int Skipped, int Failed)
{
    public static DigestRunResult NothingDue { get; } = new(null, 0, 0, 0);
}

public interface IWeeklyDigestService
{
    /// <summary>
    /// One hourly run: if a digest is due (<see cref="DigestSchedule.DuePeriod"/>),
    /// deals with up to <see cref="DigestOptions.MaxPerRun"/> users who have it
    /// switched on and have not been dealt with for that week.
    /// </summary>
    Task<DigestRunResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends the weekly digest by putting emails on the mail queue; the mail job
/// delivers them (after any sign-in links, which have a higher priority).
/// A user is dealt with once per week: the ledger row is written in the same
/// transaction as the queued email, or on its own when there was nothing to say.
/// A user whose digest fails here is left for the next hourly run; an email
/// that later fails to send is retried by the mail queue and not re-queued.
/// </summary>
public sealed class WeeklyDigestService(
    IDigestRepository digestRepository,
    IDigestComposer composer,
    IOptions<DigestOptions> digestOptions,
    IOptions<SiteOptions> siteOptions,
    TimeProvider timeProvider,
    ILogger<WeeklyDigestService> logger) : IWeeklyDigestService
{
    public async Task<DigestRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = digestOptions.Value;
        if (!options.Enabled)
            return DigestRunResult.NothingDue;

        // Startup refuses to run with the digest enabled and no base URL; this is for a test or a reload gone wrong.
        if (siteOptions.Value.NormalizedBaseUrl is not { } baseUrl)
        {
            logger.LogError("Weekly digest: Site:BaseUrl is not set; nothing was sent.");
            return DigestRunResult.NothingDue;
        }

        if (DigestSchedule.DuePeriod(timeProvider.GetUtcNow(), options) is not { } period)
            return DigestRunResult.NothingDue;

        var recipients = await digestRepository.GetDueRecipientsAsync(period, options.MaxPerRun, cancellationToken);
        if (recipients.Count == 0)
            return new DigestRunResult(period, 0, 0, 0);

        var now = AirDate.Now(timeProvider);
        var aggregates = new Dictionary<int, SeriesAggregate?>();
        var (queued, skipped, failed) = (0, 0, 0);

        foreach (var recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var digest = await composer.ComposeAsync(recipient.UserId, recipient.Username, now, baseUrl, aggregates, cancellationToken);

                if (digest.Email is null)
                {
                    // Nothing to say this week. Recorded, so the next run does not work it out again.
                    await digestRepository.RecordAsync(recipient.UserId, period, DigestSendStatus.Skipped, null, cancellationToken);
                    skipped++;
                    continue;
                }

                var email = new OutboundEmail
                {
                    Id = Guid.NewGuid(),
                    Priority = MailService.DigestPriority,
                    ToAddress = recipient.Email,
                    Subject = digest.Email.Subject,
                    Body = digest.Email.TextBody,
                    HtmlBody = digest.Email.HtmlBody,
                    ListUnsubscribeUrl = digest.OneClickUnsubscribeUrl
                };

                if (await digestRepository.RecordAsync(recipient.UserId, period, DigestSendStatus.Queued, email, cancellationToken))
                    queued++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's failure must not stop the others; no ledger row was
                // written, so the next hourly run tries this user again.
                failed++;
                logger.LogWarning(ex, "Weekly digest: failed for user {UserId}; will retry on the next run.", recipient.UserId);
            }
        }

        logger.LogInformation(
            "Weekly digest for {Period}: {Queued} queued, {Skipped} with nothing to say, {Failed} failed.",
            period, queued, skipped, failed);

        return new DigestRunResult(period, queued, skipped, failed);
    }
}
