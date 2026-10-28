using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;
using Recall.Web.Services.Push;
using Recall.Web.Services.Stats;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Account;

[Authorize]
public sealed class ProfileModel(
    ICurrentUserService currentUser,
    IStatsService statsService,
    IFavoritesService favoritesService,
    ILikeRepository likeRepository,
    IWatchlistImportRepository importRepository,
    IAppUserRepository userRepository,
    IOptions<DigestOptions> digestOptions,
    IPushSubscriptionRepository pushSubscriptions,
    IOptions<PushOptions> pushOptions,
    TimeProvider timeProvider,
    ILogger<ProfileModel> logger) : PageModel
{
    /// <summary>The weekly digest exists on this installation (<c>Digest:Enabled</c>); otherwise its switch is not shown.</summary>
    public bool DigestAvailable => digestOptions.Value.Enabled;

    /// <summary>The user has switched the weekly email on.</summary>
    public bool DigestOn { get; private set; }

    /// <summary>The day the digest goes out, for the switch's explanation.</summary>
    public DayOfWeek DigestDay => digestOptions.Value.DayOfWeek;

    /// <summary>Push notifications exist on this installation (<c>Push:Enabled</c>); otherwise the section is not shown.</summary>
    public bool PushAvailable => pushOptions.Value.Enabled;

    /// <summary>The VAPID public key the browser subscribes with.</summary>
    public string PushPublicKey => pushOptions.Value.PublicKey;

    /// <summary>The user's devices with notifications on, oldest first.</summary>
    public IReadOnlyList<PushDeviceRow> PushDevices { get; private set; } = Array.Empty<PushDeviceRow>();

    /// <summary>Pushes wait for 08:00 on each device when they would arrive between 22:00 and 08:00.</summary>
    public bool PushQuietHours { get; private set; } = true;

    /// <summary>One row of the device list.</summary>
    /// <param name="EndpointHash">
    /// SHA-256 of the endpoint, in hex: the page's script compares it with the
    /// browser's own subscription to say "This device" and to show whether
    /// notifications are on here, without the endpoint itself being in the page.
    /// </param>
    public sealed record PushDeviceRow(Guid Id, string Label, string TimeZone, string Added, string EndpointHash);

    /// <summary>Today's date in UTC, for the date format of the last import.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    /// <summary>How many liked titles the profile page previews (one row of posters) beside "See all".</summary>
    public const int FavoritesPreviewCount = 6;

    public string DisplayName => currentUser.DisplayName ?? "—";

    public string Email => currentUser.Email ?? "—";

    /// <summary>The signed-in user's role, read from the auth cookie's role claim.</summary>
    public UserRole Role =>
        Enum.TryParse<UserRole>(currentUser.Role, ignoreCase: true, out var role) ? role : UserRole.User;

    /// <summary>Label, CSS modifier and icon for the role pill shown on the page.</summary>
    public (string Label, string CssModifier, string Icon) RoleBadge => Role switch
    {
        UserRole.Admin => ("Administrator", "tvdb-role-badge--admin", Icons.Administrator),
        _ => ("Member", string.Empty, Icons.Member)
    };

    /// <summary>
    /// Total watch time, episodes and movies: the same total the Stats page
    /// shows (both come from <see cref="IStatsService"/>), and the link to it.
    /// </summary>
    public WatchTimeSummary WatchTime { get; private set; } = WatchTimeSummary.Empty;

    public IReadOnlyList<FavoriteTitle> FavoriteTitles { get; private set; } = Array.Empty<FavoriteTitle>();

    /// <summary>The user's most recent IMDb import, if they've ever run one. No items — summary only.</summary>
    public WatchlistImportJob? LatestImportJob { get; private set; }

    /// <summary>
    /// One sentence about the latest IMDb import: an invitation when there has
    /// never been one, progress while it runs, the outcome once it is done.
    /// </summary>
    public string ImportSummary
    {
        get
        {
            if (LatestImportJob is not { } job)
                return "Bring in your ratings or your watchlist from an IMDb CSV export.";

            if (job.Status == WatchlistImportJobStatus.Processing)
                return $"Importing {job.FileName}: {job.ProcessedCount} of {job.TotalCount} rows matched so far.";

            // The same words as the import page (ImportStatusDisplay). The job's
            // "skipped" counter is two outcomes in one: already had it, and unsupported.
            static string Words(WatchlistImportItemStatus status) => ImportStatusDisplay.For(status).Label.ToLowerInvariant();

            var parts = new List<string> { $"{job.ImportedCount} {Words(WatchlistImportItemStatus.Imported)}" };
            if (job.SkippedCount > 0) parts.Add($"{job.SkippedCount} skipped");
            if (job.NotFoundCount > 0) parts.Add($"{job.NotFoundCount} {Words(WatchlistImportItemStatus.NotFound)}");
            if (job.FailedCount > 0) parts.Add($"{job.FailedCount} {Words(WatchlistImportItemStatus.Failed)}");

            return $"Last import: {job.FileName}, {DisplayDate.Format(job.CreatedUtc, Today)}. {string.Join(", ", parts)}.";
        }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
            return;

        if (DigestAvailable || PushAvailable)
        {
            try
            {
                var user = await userRepository.GetByIdAsync(userId, cancellationToken);
                DigestOn = user?.DigestOptedInUtc is not null;
                PushQuietHours = user?.PushQuietHours ?? true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not load the digest and quiet-hours settings for the profile page.");
            }
        }

        if (PushAvailable)
        {
            try
            {
                PushDevices = (await pushSubscriptions.GetForUserAsync(userId, cancellationToken))
                    .Select(d => new PushDeviceRow(
                        d.Id, d.DeviceLabel, d.TimeZone, DisplayDate.Format(d.CreatedUtc, Today), EndpointHash(d.Endpoint)))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not load the push devices for the profile page.");
            }
        }

        try
        {
            WatchTime = (await statsService.GetAsync(userId, cancellationToken)).Totals.WatchTime;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compute total watch time for the profile page.");
        }

        try
        {
            FavoriteTitles = await favoritesService.GetLikedTitlesAsync(userId, FavoritesPreviewCount, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load favorite titles for the profile page.");
        }

        try
        {
            LatestImportJob = await importRepository.GetLatestJobForUserAsync(userId, includeItems: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the latest watchlist import job for the profile page.");
        }
    }

    /// <summary>The "Weekly email" switch. Opt-in: nothing is sent to anyone who has not turned it on.</summary>
    public async Task<IActionResult> OnPostSetDigestAsync(bool on, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !DigestAvailable)
            return RedirectToPage();

        try
        {
            await userRepository.SetDigestOptInAsync(userId, on, cancellationToken);
            this.SetSuccessToast(on
                ? $"Weekly email is on. It arrives on {DigestDay}s, when there is something new."
                : "Weekly email is off.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed changing the digest setting for user {UserId}.", userId);
            this.SetErrorToast("Could not change that setting right now.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// "Notifications on this device": the page's script has asked the browser
    /// for permission (only after the user pressed the button) and subscribed
    /// it, and posts the subscription and the device's time zone here.
    /// </summary>
    public async Task<IActionResult> OnPostSubscribeDeviceAsync(
        string? endpoint, string? p256dh, string? auth, string? timeZone, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !PushAvailable)
            return RedirectToPage();

        switch (PushSubscriptionInput.Check(endpoint, p256dh, auth))
        {
            case PushSubscriptionInput.Problem.UnknownService:
                logger.LogWarning("Refused a push subscription for user {UserId}: not a known push service.", userId);
                this.SetErrorToast("This browser's notification service isn't one Recall can send to, so notifications can't be turned on here.");
                return RedirectToPage();
            case PushSubscriptionInput.Problem.BadKeys:
                logger.LogWarning("Refused a push subscription for user {UserId}: malformed keys.", userId);
                this.SetErrorToast("Could not turn on notifications on this device. Try again.");
                return RedirectToPage();
        }

        try
        {
            await pushSubscriptions.SaveAsync(userId, new NewPushSubscription(
                endpoint!.Trim(),
                p256dh!.Trim(),
                auth!.Trim(),
                DeviceLabel.FromUserAgent(Request.Headers.UserAgent.ToString()),
                DeviceTimeZone.Normalize(timeZone)), cancellationToken);
            this.SetSuccessToast("Notifications are on for this device.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed saving a push subscription for user {UserId}.", userId);
            this.SetErrorToast("Could not turn on notifications on this device. Try again.");
        }

        return RedirectToPage();
    }

    /// <summary>"Turn off" on this device: the script has unsubscribed the browser and posts its endpoint.</summary>
    public async Task<IActionResult> OnPostUnsubscribeDeviceAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !PushAvailable)
            return RedirectToPage();

        try
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
                await pushSubscriptions.RemoveByEndpointAsync(userId, endpoint.Trim(), cancellationToken);
            this.SetSuccessToast("Notifications are off for this device.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed removing a push subscription for user {UserId}.", userId);
            this.SetErrorToast("Could not turn off notifications right now.");
        }

        return RedirectToPage();
    }

    /// <summary>"Remove" on a row of the device list, for any of the user's devices.</summary>
    public async Task<IActionResult> OnPostRemoveDeviceAsync(Guid id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !PushAvailable)
            return RedirectToPage();

        try
        {
            if (await pushSubscriptions.RemoveAsync(userId, id, cancellationToken))
                this.SetSuccessToast("Device removed. It gets no more notifications.");
            else
                this.SetInfoToast("That device was already removed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed removing push device {DeviceId} for user {UserId}.", id, userId);
            this.SetErrorToast("Could not remove that device right now.");
        }

        return RedirectToPage();
    }

    /// <summary>The "Quiet hours" switch: one setting for all of the user's devices, each in its own time zone.</summary>
    public async Task<IActionResult> OnPostSetQuietHoursAsync(bool on, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !PushAvailable)
            return RedirectToPage();

        try
        {
            await userRepository.SetPushQuietHoursAsync(userId, on, cancellationToken);
            this.SetSuccessToast(on
                ? "Quiet hours are on. Notifications due between 22:00 and 08:00 on a device wait until 08:00 there."
                : "Quiet hours are off. Notifications arrive whatever the time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed changing quiet hours for user {UserId}.", userId);
            this.SetErrorToast("Could not change that setting right now.");
        }

        return RedirectToPage();
    }

    private static string EndpointHash(string endpoint) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint)));

    public async Task<IActionResult> OnPostToggleSeriesLikeAsync(int id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            this.SetErrorToast("You need to be signed in to like a series.");
            return RedirectToPage();
        }

        try
        {
            await likeRepository.ToggleAsync(userId, LikeTargetType.Series, id, id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for series {SeriesId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleMovieLikeAsync(int id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            this.SetErrorToast("You need to be signed in to like a movie.");
            return RedirectToPage();
        }

        try
        {
            await likeRepository.ToggleAsync(userId, LikeTargetType.Movie, id, id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed toggling like for movie {MovieId}.", id);
            this.SetErrorToast("Could not update your like right now.");
        }

        return RedirectToPage();
    }
}
