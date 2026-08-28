using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class CachedEpisodeExtendedEntityConfiguration : IEntityTypeConfiguration<CachedEpisodeExtendedEntity>
{
    public void Configure(EntityTypeBuilder<CachedEpisodeExtendedEntity> builder)
    {
        builder.ToTable("cached_episode_extended");

        builder.HasKey(x => x.EpisodeTvdbId);

        builder.Property(x => x.EpisodeTvdbId)
            .HasColumnName("episode_tvdb_id")
            .ValueGeneratedNever();

        builder.Property(x => x.SeriesTvdbId).HasColumnName("series_tvdb_id");

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(500);

        builder.Property(x => x.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.RetrievedUtc)
            .HasColumnName("retrieved_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
