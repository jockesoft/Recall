using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class AppUserEntityConfiguration : IEntityTypeConfiguration<AppUserEntity>
{
    public void Configure(EntityTypeBuilder<AppUserEntity> builder)
    {
        builder.ToTable("app_user");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .HasMaxLength(200)
            .IsRequired();

        builder.HasIndex(x => x.UserId).IsUnique();

        builder.Property(x => x.Username)
            .HasColumnName("user_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Email)
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        builder.HasIndex(x => x.Email);

        builder.Property(x => x.Password)
            .HasColumnName("password_hash")
            .HasMaxLength(130)
            .IsRequired();
        
        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        
        builder.HasIndex(x => x.UserId).IsUnique();
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.Username).IsUnique();
    }
}