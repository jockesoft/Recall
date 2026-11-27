using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class SiteMetricDailyEntityConfiguration : IEntityTypeConfiguration<SiteMetricDailyEntity>
{
    public void Configure(EntityTypeBuilder<SiteMetricDailyEntity> builder)
    {
        builder.ToTable("site_metric_daily");

        // One row per day and key: the flush adds to it.
        builder.HasKey(x => new { x.Date, x.Key });

        builder.Property(x => x.Date)
            .HasColumnName("date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.Key)
            .HasColumnName("key")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Count)
            .HasColumnName("count")
            .IsRequired();
    }
}
