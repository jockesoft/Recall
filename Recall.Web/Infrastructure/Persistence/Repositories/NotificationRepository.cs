using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class NotificationRepository(
    AppDbContext dbContext,
    ILogger<NotificationRepository> logger)
    : INotificationRepository
{
    public async Task<IReadOnlyList<Notification>> GetForUserAsync(
        Guid userId,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Notifications
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(take)
            .Select(x => new Notification(
                x.Id, x.Type, x.Title, x.Body, x.SeriesTvdbId, x.EpisodeTvdbId, x.EpisodeCount,
                x.IsRead, x.CreatedUtc, x.ReadUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<Notification?> GetAsync(
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Notifications
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Id == notificationId)
            .Select(x => new Notification(
                x.Id, x.Type, x.Title, x.Body, x.SeriesTvdbId, x.EpisodeTvdbId, x.EpisodeCount,
                x.IsRead, x.CreatedUtc, x.ReadUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return dbContext.Notifications
            .AsNoTracking()
            .CountAsync(x => x.UserId == userId && !x.IsRead, cancellationToken);
    }

    public async Task<IReadOnlySet<int>> GetAlreadyNotifiedEpisodeIdsAsync(
        Guid userId,
        IEnumerable<int> episodeTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var ids = episodeTvdbIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new HashSet<int>();

        var found = await dbContext.NotifiedEpisodes
            .AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.EpisodeTvdbId))
            .Select(x => x.EpisodeTvdbId)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }

    public async Task<bool> AddNewEpisodeNotificationAsync(
        Guid userId,
        int seriesTvdbId,
        int linkEpisodeTvdbId,
        int episodeCount,
        string title,
        string? body,
        IReadOnlyCollection<int> coveredEpisodeTvdbIds,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        dbContext.Notifications.Add(new NotificationEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.NewEpisode,
            Title = title,
            Body = body,
            SeriesTvdbId = seriesTvdbId,
            EpisodeTvdbId = linkEpisodeTvdbId,
            EpisodeCount = episodeCount,
            IsRead = false
        });

        foreach (var episodeId in coveredEpisodeTvdbIds.Distinct())
        {
            dbContext.NotifiedEpisodes.Add(new NotifiedEpisodeEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SeriesTvdbId = seriesTvdbId,
                EpisodeTvdbId = episodeId,
                CreatedUtc = now
            });
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent sweep already claimed one of these episodes for this
            // user. Nothing to do — they've been notified.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(
                "New-episode notification for user {UserId}, series {SeriesId} lost a race with a concurrent sweep.",
                userId, seriesTvdbId);
            return false;
        }
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Notifications
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Id == notificationId, cancellationToken);

        if (entity is null || entity.IsRead)
            return;

        entity.IsRead = true;
        entity.ReadUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await dbContext.Notifications
            .Where(x => x.UserId == userId && !x.IsRead)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.IsRead, true)
                      .SetProperty(x => x.ReadUtc, now)
                      .SetProperty(x => x.UpdatedUtc, now),
                cancellationToken);
    }
}
