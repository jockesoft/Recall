using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class EpisodeWatchEntityConfiguration : IEntityTypeConfiguration<EpisodeWatchEntity>
{
    public void Configure(EntityTypeBuilder<EpisodeWatchEntity> builder)
    {
        builder.ToTable("episode_watch");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.EpisodeWatches)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.SeriesTvdbId)
            .HasColumnName("series_tvdb_id")
            .IsRequired();

        builder.Property(x => x.EpisodeTvdbId)
            .HasColumnName("episode_tvdb_id")
            .IsRequired();

        builder.HasIndex(x => new { x.UserId, x.EpisodeTvdbId })
            .IsUnique();

        builder.HasIndex(x => new { x.UserId, x.SeriesTvdbId });

        builder.Property(x => x.WatchedUtc)
            .HasColumnName("watched_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
