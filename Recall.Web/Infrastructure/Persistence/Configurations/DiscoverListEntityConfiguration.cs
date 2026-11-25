using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class DiscoverListEntityConfiguration : IEntityTypeConfiguration<DiscoverListEntity>
{
    public void Configure(EntityTypeBuilder<DiscoverListEntity> builder)
    {
        builder.ToTable("discover_list");

        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key)
            .HasColumnName("key")
            .HasMaxLength(64);

        builder.Property(x => x.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.ItemCount)
            .HasColumnName("item_count")
            .IsRequired();

        builder.Property(x => x.FetchedUtc)
            .HasColumnName("fetched_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
