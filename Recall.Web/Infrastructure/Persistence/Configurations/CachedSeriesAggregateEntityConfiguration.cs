using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class CachedSeriesAggregateEntityConfiguration : IEntityTypeConfiguration<CachedSeriesAggregateEntity>
{
    public void Configure(EntityTypeBuilder<CachedSeriesAggregateEntity> builder)
    {
        builder.ToTable("cached_series_aggregate");

        builder.HasKey(x => new { x.TvdbId, x.Language });

        builder.Property(x => x.TvdbId).HasColumnName("tvdb_id");

        builder.Property(x => x.Language)
            .HasColumnName("language")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.StatusName)
            .HasColumnName("status_name")
            .HasMaxLength(100);

        builder.Property(x => x.KeepUpdated).HasColumnName("keep_updated");

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
