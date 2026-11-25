using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class DiscoverDismissalEntityConfiguration : IEntityTypeConfiguration<DiscoverDismissalEntity>
{
    public void Configure(EntityTypeBuilder<DiscoverDismissalEntity> builder)
    {
        builder.ToTable("discover_dismissal");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<AppUserEntity>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.TvdbId)
            .HasColumnName("tvdb_id")
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One "not interested" per user and title; it also serves "this user's hidden titles".
        builder.HasIndex(x => new { x.UserId, x.Kind, x.TvdbId })
            .IsUnique();
    }
}
