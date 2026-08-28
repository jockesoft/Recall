using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class CachedSeriesExtendedEntityConfiguration : IEntityTypeConfiguration<CachedSeriesExtendedEntity>
{
    public void Configure(EntityTypeBuilder<CachedSeriesExtendedEntity> builder)
    {
        builder.ToTable("cached_series_extended");

        builder.HasKey(x => x.TvdbId);

        builder.Property(x => x.TvdbId)
            .HasColumnName("tvdb_id")
            .ValueGeneratedNever();

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
