using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class DigestSendEntityConfiguration : IEntityTypeConfiguration<DigestSendEntity>
{
    public void Configure(EntityTypeBuilder<DigestSendEntity> builder)
    {
        builder.ToTable("digest_send");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<AppUserEntity>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.PeriodStart)
            .HasColumnName("period_start")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // The dedupe guard: one row per user and week.
        builder.HasIndex(x => new { x.UserId, x.PeriodStart })
            .IsUnique();

        // The retention delete scans by age.
        builder.HasIndex(x => x.CreatedUtc);
    }
}
