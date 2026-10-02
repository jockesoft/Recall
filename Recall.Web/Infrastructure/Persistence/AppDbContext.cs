using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
//    public DbSet<AppUserEntity> Users => Set<AppUserEntity>();
    public DbSet<TrackedSeriesEntity> TrackedSeries => Set<TrackedSeriesEntity>();
    public DbSet<TrackedMovieEntity> TrackedMovies => Set<TrackedMovieEntity>();
    public DbSet<AppUserEntity> AppUsers => Set<AppUserEntity>();
    public DbSet<EpisodeWatchEntity> EpisodeWatches => Set<EpisodeWatchEntity>();
    public DbSet<UserLikeEntity> UserLikes => Set<UserLikeEntity>();
    public DbSet<UserMovieWatchEntity> UserMovieWatches => Set<UserMovieWatchEntity>();
    public DbSet<UserRatingEntity> UserRatings => Set<UserRatingEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<NotifiedEpisodeEntity> NotifiedEpisodes => Set<NotifiedEpisodeEntity>();
    public DbSet<EmailEntity> Emails => Set<EmailEntity>();
    public DbSet<LoginTokenEntity> LoginTokens => Set<LoginTokenEntity>();
    public DbSet<DigestSendEntity> DigestSends => Set<DigestSendEntity>();
    public DbSet<WatchlistImportJobEntity> WatchlistImportJobs => Set<WatchlistImportJobEntity>();
    public DbSet<WatchlistImportItemEntity> WatchlistImportItems => Set<WatchlistImportItemEntity>();

    // Durable TheTVDB snapshots — fallback tier below Redis (read: cache -> DB -> API).
    public DbSet<CachedSeriesAggregateEntity> CachedSeriesAggregates => Set<CachedSeriesAggregateEntity>();
    public DbSet<CachedSeriesExtendedEntity> CachedSeriesExtended => Set<CachedSeriesExtendedEntity>();
    public DbSet<CachedEpisodeExtendedEntity> CachedEpisodesExtended => Set<CachedEpisodeExtendedEntity>();
    public DbSet<CachedMovieAggregateEntity> CachedMovieAggregates => Set<CachedMovieAggregateEntity>();

    // OMDb enrichment snapshot per series, refreshed at most monthly by UpdateOmdbInfoTimer.
    public DbSet<CachedSeriesOmdbEntity> CachedSeriesOmdb => Set<CachedSeriesOmdbEntity>();
    public DbSet<CachedEpisodeOmdbEntity> CachedEpisodesOmdb => Set<CachedEpisodeOmdbEntity>();
    public DbSet<CachedMovieOmdbEntity> CachedMoviesOmdb => Set<CachedMovieOmdbEntity>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        return base.SaveChanges();
    }

    /// <summary>
    /// Stamps every tracked <see cref="IHasAuditTimestamps"/> entity. An entity
    /// opts in by implementing the interface — there is no list to keep in sync here.
    /// </summary>
    private void ApplyAuditTimestamps()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IHasAuditTimestamps>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedUtc = now;
                entry.Entity.UpdatedUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedUtc = now;
            }
        }
    }
}
