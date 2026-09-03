using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Notifications.Models;

namespace Recall.Web.Services.Notifications;

public sealed class NotificationService(
    INotificationRepository notificationRepository,
    ILogger<NotificationService> logger)
    : INotificationService
{
    /// <summary>Upper bound on rows the Notifications page renders.</summary>
    private const int RecentTake = 50;

    public async Task<bool> NotifyNewEpisodesAsync(
        Guid userId,
        NewEpisodesDigest digest,
        CancellationToken cancellationToken = default)
    {
        if (digest.Episodes.Count == 0)
            return false;

        var candidateIds = digest.Episodes.Select(e => e.EpisodeTvdbId).ToList();
        var alreadyNotified = await notificationRepository.GetAlreadyNotifiedEpisodeIdsAsync(
            userId, candidateIds, cancellationToken);

        var fresh = digest.Episodes
            .Where(e => !alreadyNotified.Contains(e.EpisodeTvdbId))
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeTvdbId)
            .ToList();

        if (fresh.Count == 0)
            return false;

        var (title, body) = BuildCopy(digest.SeriesName, fresh);

        var created = await notificationRepository.AddNewEpisodeNotificationAsync(
            userId,
            digest.SeriesTvdbId,
            linkEpisodeTvdbId: fresh[0].EpisodeTvdbId,
            episodeCount: fresh.Count,
            title: title,
            body: body,
            coveredEpisodeTvdbIds: fresh.Select(e => e.EpisodeTvdbId).ToList(),
            cancellationToken);

        if (created)
            logger.LogInformation(
                "Notified user {UserId} about {Count} new episode(s) of series {SeriesId}.",
                userId, fresh.Count, digest.SeriesTvdbId);

        return created;
    }

    public async Task<IReadOnlyList<NotificationListItem>> GetRecentAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await notificationRepository.GetForUserAsync(userId, RecentTake, cancellationToken);
        return rows.Select(ToListItem).ToList();
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
        => notificationRepository.GetUnreadCountAsync(userId, cancellationToken);

    public async Task<string?> OpenAsync(
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var notification = await notificationRepository.GetAsync(userId, notificationId, cancellationToken);
        if (notification is null)
            return null;

        await notificationRepository.MarkReadAsync(userId, notificationId, cancellationToken);
        return BuildTargetHref(notification);
    }

    public Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
        => notificationRepository.MarkAllReadAsync(userId, cancellationToken);

    private static NotificationListItem ToListItem(Notification n) =>
        new(n.Id, n.Type, n.Title, n.Body, n.EpisodeCount, n.IsRead, n.CreatedUtc, BuildTargetHref(n));

    /// <summary>
    /// Local URL a notification links to. Kept here (not on the entity) so the
    /// route lives next to the page that owns it.
    /// </summary>
    private static string? BuildTargetHref(Notification n) => n.Type switch
    {
        NotificationType.NewEpisode when n.EpisodeTvdbId is > 0
            => $"/Episodes/Details/{n.EpisodeTvdbId}",
        _ => null
    };

    // -- copy -------------------------------------------------------------

    private static (string Title, string? Body) BuildCopy(
        string seriesName,
        IReadOnlyList<NewEpisodeItem> orderedEpisodes)
    {
        if (orderedEpisodes.Count == 1)
            return ($"New episode of {seriesName}", BuildSingleBody(orderedEpisodes[0]));

        var range = BuildRangeLabel(orderedEpisodes[0], orderedEpisodes[^1]);
        var body = range is null
            ? $"{orderedEpisodes.Count} new episodes are out now."
            : $"{range} are out now.";

        return ($"{orderedEpisodes.Count} new episodes of {seriesName}", body);
    }

    private static string BuildSingleBody(NewEpisodeItem episode)
    {
        var name = CleanName(episode.EpisodeName);

        return (episode.SlateCode, name) switch
        {
            (not null, not null) => $"{episode.SlateCode} · {name} is out now.",
            (not null, null) => $"{episode.SlateCode} is out now.",
            (null, not null) => $"“{name}” is out now.",
            _ => "A new episode is out now."
        };
    }

    /// <summary>
    /// "S02 · E01–E08" when the batch is all one season, "S01E10–S02E03" when it
    /// spans seasons, <c>null</c> when the numbers aren't known.
    /// </summary>
    private static string? BuildRangeLabel(NewEpisodeItem first, NewEpisodeItem last)
    {
        if (first is not { SeasonNumber: { } fs, EpisodeNumber: { } fe } ||
            last is not { SeasonNumber: { } ls, EpisodeNumber: { } le })
            return null;

        return fs == ls
            ? $"S{fs:D2} · E{fe:D2}–E{le:D2}"
            : $"S{fs:D2}E{fe:D2}–S{ls:D2}E{le:D2}";
    }

    private static string? CleanName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(name.Trim(), "TBA", StringComparison.OrdinalIgnoreCase)
            ? null
            : name.Trim();
}
