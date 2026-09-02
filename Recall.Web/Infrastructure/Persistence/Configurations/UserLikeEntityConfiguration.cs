using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class UserLikeEntityConfiguration : IEntityTypeConfiguration<UserLikeEntity>
{
    public void Configure(EntityTypeBuilder<UserLikeEntity> builder)
    {
        builder.ToTable("user_like");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.Likes)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.TargetType)
            .HasColumnName("target_type")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.TargetTvdbId)
            .HasColumnName("target_tvdb_id")
            .IsRequired();

        builder.Property(x => x.SeriesTvdbId)
            .HasColumnName("series_tvdb_id")
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One like per user per target.
        builder.HasIndex(x => new { x.UserId, x.TargetType, x.TargetTvdbId })
            .IsUnique();

        // "This user's liked series / liked episodes".
        builder.HasIndex(x => new { x.UserId, x.TargetType });

        // "Likes for this series' episodes".
        builder.HasIndex(x => new { x.UserId, x.SeriesTvdbId });

        // Future: like counts per target.
        builder.HasIndex(x => new { x.TargetType, x.TargetTvdbId });
    }
}
