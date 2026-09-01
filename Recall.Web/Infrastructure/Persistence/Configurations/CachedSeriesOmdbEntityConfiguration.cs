using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class CachedSeriesOmdbEntityConfiguration : IEntityTypeConfiguration<CachedSeriesOmdbEntity>
{
    public void Configure(EntityTypeBuilder<CachedSeriesOmdbEntity> builder)
    {
        builder.ToTable("cached_series_omdb");

        builder.HasKey(x => x.TvdbId);

        builder.Property(x => x.TvdbId)
            .HasColumnName("tvdb_id")
            .ValueGeneratedNever();

        builder.Property(x => x.ImdbId)
            .HasColumnName("imdb_id")
            .HasMaxLength(32);

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(500);

        builder.Property(x => x.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb");

        builder.Property(x => x.RetrievedUtc)
            .HasColumnName("retrieved_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(x => x.RetrievedUtc)
            .HasDatabaseName("ix_cached_series_omdb_retrieved_utc");
    }
}
