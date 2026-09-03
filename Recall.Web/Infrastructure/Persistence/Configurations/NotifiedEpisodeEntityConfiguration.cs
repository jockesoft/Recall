using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class NotifiedEpisodeEntityConfiguration : IEntityTypeConfiguration<NotifiedEpisodeEntity>
{
    public void Configure(EntityTypeBuilder<NotifiedEpisodeEntity> builder)
    {
        builder.ToTable("notified_episode");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<AppUserEntity>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.SeriesTvdbId)
            .HasColumnName("series_tvdb_id")
            .IsRequired();

        builder.Property(x => x.EpisodeTvdbId)
            .HasColumnName("episode_tvdb_id")
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // The dedupe guard, and the shape of the "have we told this user yet?" lookup.
        builder.HasIndex(x => new { x.UserId, x.EpisodeTvdbId })
            .IsUnique();
    }
}
