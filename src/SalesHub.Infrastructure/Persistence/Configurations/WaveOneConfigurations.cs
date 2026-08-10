using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).HasMaxLength(128).IsRequired();
        builder.Property(b => b.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.HasIndex(b => b.Name).IsUnique();
    }
}

public sealed class PasswordResetRequestConfiguration
    : IEntityTypeConfiguration<PasswordResetRequest>
{
    public void Configure(EntityTypeBuilder<PasswordResetRequest> builder)
    {
        builder.ToTable("password_reset_requests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.UsernameSubmitted).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(r => new { r.Status, r.CreatedAtUtc });
    }
}

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Category).HasMaxLength(64).IsRequired();
        builder.Property(n => n.Title).HasMaxLength(256).IsRequired();
        builder.Property(n => n.SafePreview).HasMaxLength(512);
        builder.Property(n => n.ReferenceType).HasMaxLength(64);
        builder.Property(n => n.ReferenceId).HasMaxLength(64);
        // The inbox query: a user's notifications, newest first, unread filter.
        builder.HasIndex(n => new { n.UserId, n.CreatedAtUtc });
        builder.HasIndex(n => new { n.UserId, n.ReadAtUtc });
    }
}

public sealed class NotificationDeliveryConfiguration
    : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Channel).HasMaxLength(32).IsRequired();
        builder.Property(d => d.State).HasMaxLength(32).IsRequired();
        builder.HasIndex(d => d.NotificationId);
    }
}

public sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("push_subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Endpoint).HasMaxLength(2048).IsRequired();
        builder.Property(s => s.P256dh).HasMaxLength(256).IsRequired();
        builder.Property(s => s.Auth).HasMaxLength(256).IsRequired();
        builder.HasIndex(s => new { s.UserId, s.Active });
        builder.HasIndex(s => s.Endpoint).IsUnique();
    }
}
