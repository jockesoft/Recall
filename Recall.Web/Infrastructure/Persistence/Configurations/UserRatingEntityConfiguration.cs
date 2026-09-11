using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class UserRatingEntityConfiguration : IEntityTypeConfiguration<UserRatingEntity>
{
    public void Configure(EntityTypeBuilder<UserRatingEntity> builder)
    {
        builder.ToTable("user_rating", t =>
            t.HasCheckConstraint("ck_user_rating_value_range", "value >= 1 AND value <= 10"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.Ratings)
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

        builder.Property(x => x.Value)
            .HasColumnName("value")
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One rating per user per target.
        builder.HasIndex(x => new { x.UserId, x.TargetType, x.TargetTvdbId })
            .IsUnique();

        // "This user's rated series / rated episodes".
        builder.HasIndex(x => new { x.UserId, x.TargetType });

        // "Ratings for this series' episodes".
        builder.HasIndex(x => new { x.UserId, x.SeriesTvdbId });

        // Future: average rating per target.
        builder.HasIndex(x => new { x.TargetType, x.TargetTvdbId });
    }
}
