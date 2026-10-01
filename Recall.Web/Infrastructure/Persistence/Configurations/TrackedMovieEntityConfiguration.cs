using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class TrackedMovieEntityConfiguration : IEntityTypeConfiguration<TrackedMovieEntity>
{
    public void Configure(EntityTypeBuilder<TrackedMovieEntity> builder)
    {
        builder.ToTable("tracked_movie");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.TrackedMovies)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.TvdbId)
            .HasColumnName("tvdb_id")
            .IsRequired();

        // Unique per user, not globally unique.
        builder.HasIndex(x => new { x.UserId, x.TvdbId })
            .IsUnique();

        // The refresh job asks "is this movie on anyone's watchlist?" by id alone.
        builder.HasIndex(x => x.TvdbId);

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
