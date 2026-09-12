using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class WatchlistImportJobEntityConfiguration : IEntityTypeConfiguration<WatchlistImportJobEntity>
{
    public void Configure(EntityTypeBuilder<WatchlistImportJobEntity> builder)
    {
        builder.ToTable("watchlist_import_job");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.WatchlistImportJobs)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(260)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.TotalCount).HasColumnName("total_count").IsRequired();
        builder.Property(x => x.ProcessedCount).HasColumnName("processed_count").IsRequired();
        builder.Property(x => x.ImportedCount).HasColumnName("imported_count").IsRequired();
        builder.Property(x => x.SkippedCount).HasColumnName("skipped_count").IsRequired();
        builder.Property(x => x.NotFoundCount).HasColumnName("not_found_count").IsRequired();
        builder.Property(x => x.FailedCount).HasColumnName("failed_count").IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.CompletedUtc)
            .HasColumnName("completed_utc")
            .HasColumnType("timestamp with time zone");

        // One active import at a time is enforced in the service layer; this
        // index just makes "does this user have one" / "their latest job" fast.
        builder.HasIndex(x => new { x.UserId, x.CreatedUtc });
    }
}
