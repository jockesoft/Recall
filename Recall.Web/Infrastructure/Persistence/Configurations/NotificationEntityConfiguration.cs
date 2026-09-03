using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class NotificationEntityConfiguration : IEntityTypeConfiguration<NotificationEntity>
{
    public void Configure(EntityTypeBuilder<NotificationEntity> builder)
    {
        builder.ToTable("notification");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.Notifications)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.Title)
            .HasColumnName("title")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.Body)
            .HasColumnName("body")
            .HasMaxLength(1024);

        builder.Property(x => x.SeriesTvdbId)
            .HasColumnName("series_tvdb_id");

        builder.Property(x => x.EpisodeTvdbId)
            .HasColumnName("episode_tvdb_id");

        builder.Property(x => x.EpisodeCount)
            .HasColumnName("episode_count")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(x => x.IsRead)
            .HasColumnName("is_read")
            .IsRequired();

        builder.Property(x => x.ReadUtc)
            .HasColumnName("read_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // "This user's notifications, newest first" — the list page and the bell.
        builder.HasIndex(x => new { x.UserId, x.CreatedUtc });

        // Unread-count lookups run on every authenticated page render.
        builder.HasIndex(x => new { x.UserId, x.IsRead });

        // Dedupe for "new episode" alerts lives on the notified_episode ledger,
        // not here — one notification can now cover several episodes.
    }
}
