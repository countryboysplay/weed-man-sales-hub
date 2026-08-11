using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcements");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Title).HasMaxLength(256).IsRequired();
        builder.Property(a => a.Body).HasMaxLength(16000);
        builder.Property(a => a.Priority).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(a => a.PublishedAtUtc);
        builder.HasIndex(a => a.ScheduledPublishAtUtc)
            .HasFilter("published_at_utc IS NULL AND archived_at_utc IS NULL");
        builder.HasIndex(a => a.PinRank);
    }
}

public sealed class AnnouncementTargetConfiguration : IEntityTypeConfiguration<AnnouncementTarget>
{
    public void Configure(EntityTypeBuilder<AnnouncementTarget> builder)
    {
        builder.ToTable("announcement_targets");
        builder.HasKey(t => new { t.AnnouncementId, t.UserId });
        builder.HasIndex(t => t.UserId); // "my feed"
    }
}
