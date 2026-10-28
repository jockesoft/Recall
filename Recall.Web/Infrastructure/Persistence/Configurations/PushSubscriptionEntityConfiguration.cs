using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Configurations;

public sealed class PushSubscriptionEntityConfiguration : IEntityTypeConfiguration<PushSubscriptionEntity>
{
    public void Configure(EntityTypeBuilder<PushSubscriptionEntity> builder)
    {
        builder.ToTable("push_subscription");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(x => x.PushSubscriptions)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(x => x.P256dh)
            .HasColumnName("p256dh")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Auth)
            .HasColumnName("auth")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.DeviceLabel)
            .HasColumnName("device_label")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.TimeZone)
            .HasColumnName("time_zone")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.LastSuccessUtc)
            .HasColumnName("last_success_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.HeldSinceUtc)
            .HasColumnName("held_since_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.HeldUntilUtc)
            .HasColumnName("held_until_utc")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.CreatedUtc)
            .HasColumnName("created_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .HasColumnName("updated_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One row per browser: subscribing again updates it.
        builder.HasIndex(x => x.Endpoint).IsUnique();

        // "This user's devices": the Profile list and every push to a user.
        builder.HasIndex(x => x.UserId);
    }
}
