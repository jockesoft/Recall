using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class UserMovieWatchEntityConfiguration : IEntityTypeConfiguration<UserMovieWatchEntity>
{
    public void Configure(EntityTypeBuilder<UserMovieWatchEntity> builder)
    {
        builder.ToTable("user_movie_watch");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.MovieWatches)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.MovieTvdbId)
            .HasColumnName("movie_tvdb_id")
            .IsRequired();

        builder.HasIndex(x => new { x.UserId, x.MovieTvdbId })
            .IsUnique();

        builder.Property(x => x.WatchedUtc)
            .HasColumnName("watched_utc")
            .HasColumnType("timestamp with time zone")
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
