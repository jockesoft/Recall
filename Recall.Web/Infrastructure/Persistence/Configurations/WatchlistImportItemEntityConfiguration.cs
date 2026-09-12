using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class WatchlistImportItemEntityConfiguration : IEntityTypeConfiguration<WatchlistImportItemEntity>
{
    public void Configure(EntityTypeBuilder<WatchlistImportItemEntity> builder)
    {
        builder.ToTable("watchlist_import_item");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.JobId)
            .HasColumnName("job_id")
            .IsRequired();

        builder.HasOne(x => x.Job)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.RowNumber).HasColumnName("row_number").IsRequired();

        builder.Property(x => x.ImdbId)
            .HasColumnName("imdb_id")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Title)
            .HasColumnName("title")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.TitleType)
            .HasColumnName("title_type")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.YourRating).HasColumnName("your_rating");

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.ResolvedTvdbId).HasColumnName("resolved_tvdb_id");

        builder.Property(x => x.ResultMessage)
            .HasColumnName("result_message")
            .HasMaxLength(500);

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.ProcessedUtc)
            .HasColumnName("processed_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(x => x.JobId);

        // The timer's core query: oldest pending rows across every user's job.
        builder.HasIndex(x => new { x.Status, x.CreatedUtc });
    }
}
