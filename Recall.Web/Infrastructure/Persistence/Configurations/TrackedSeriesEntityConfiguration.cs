using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class TrackedSeriesEntityConfiguration : IEntityTypeConfiguration<TrackedSeriesEntity>
{
    public void Configure(EntityTypeBuilder<TrackedSeriesEntity> builder)
    {
        builder.ToTable("tracked_series");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.TrackedSeries)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.TvdbId)
            .HasColumnName("tvdb_id")
            .IsRequired();

        // Important: unique per user, not globally unique.
        builder.HasIndex(x => new { x.UserId, x.TvdbId })
            .IsUnique();

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.Overview).HasColumnName("overview");

        builder.Property(x => x.ImageUrl)
            .HasColumnName("image_url")
            .HasMaxLength(1000);

        builder.Property(x => x.FirstAired).HasColumnName("first_aired");

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion()
            .ValueGeneratedOnAddOrUpdate();
    }
}