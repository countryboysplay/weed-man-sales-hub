using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.DeviceId).HasMaxLength(128);
        builder.Property(s => s.BrowserFamily).HasMaxLength(128);
        builder.Property(s => s.OsFamily).HasMaxLength(128);
        builder.Property(s => s.AppVersion).HasMaxLength(64);
        builder.Property(s => s.IpHash).HasMaxLength(64);
        builder.Property(s => s.IdleCapabilityState).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.RevokeReason).HasConversion<string>().HasMaxLength(64);
        builder.HasIndex(s => new { s.UserId, s.RevokedAtUtc });
        builder.HasIndex(s => s.LastSeenAtUtc);
        // The stale-capability sweep scans verified sessions by lease expiry.
        builder.HasIndex(s => s.IdleCapabilityLeaseUntilUtc)
            .HasFilter("revoked_at_utc IS NULL");
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Category).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(160).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(64);
        builder.Property(a => a.TargetId).HasMaxLength(64);
        builder.Property(a => a.PublicRecordId).HasMaxLength(24);
        builder.Property(a => a.DeviceId).HasMaxLength(128);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.RetentionClass).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.BeforeJson).HasColumnType("jsonb");
        builder.Property(a => a.AfterJson).HasColumnType("jsonb");
        builder.HasIndex(a => a.OccurredAtUtc);
        builder.HasIndex(a => new { a.Category, a.OccurredAtUtc });
        builder.HasIndex(a => new { a.ActorUserId, a.OccurredAtUtc });
        builder.HasIndex(a => new { a.TargetType, a.TargetId });
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.EventType).HasMaxLength(160).IsRequired();
        builder.Property(m => m.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(64);
        // Partial index for the dispatcher's pending scan (db/schema-notes.sql).
        builder.HasIndex(m => m.AvailableAtUtc)
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("processed_at_utc IS NULL");
    }
}

public sealed class PublicIdSequenceConfiguration : IEntityTypeConfiguration<PublicIdSequence>
{
    public void Configure(EntityTypeBuilder<PublicIdSequence> builder)
    {
        builder.ToTable("public_id_sequences");
        builder.HasKey(s => new { s.Prefix, s.Year });
        builder.Property(s => s.Prefix).HasMaxLength(16);
    }
}

public sealed class ScheduledJobConfiguration : IEntityTypeConfiguration<ScheduledJob>
{
    public void Configure(EntityTypeBuilder<ScheduledJob> builder)
    {
        builder.ToTable("scheduled_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.JobKey).HasMaxLength(128).IsRequired();
        builder.Property(j => j.JobType).HasMaxLength(128).IsRequired();
        builder.Property(j => j.CronExpression).HasMaxLength(128).IsRequired();
        builder.Property(j => j.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(j => j.LeaseOwner).HasMaxLength(128);
        builder.HasIndex(j => j.JobKey).IsUnique();
        builder.HasIndex(j => j.NextRunAtUtc).HasFilter("enabled");
    }
}

public sealed class ScheduledJobRunConfiguration : IEntityTypeConfiguration<ScheduledJobRun>
{
    public void Configure(EntityTypeBuilder<ScheduledJobRun> builder)
    {
        builder.ToTable("scheduled_job_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ErrorClass).HasMaxLength(256);
        builder.Property(r => r.CorrelationId).HasMaxLength(64);
        builder.HasIndex(r => new { r.JobId, r.StartedAtUtc });
    }
}

public sealed class FileBlobConfiguration : IEntityTypeConfiguration<FileBlob>
{
    public void Configure(EntityTypeBuilder<FileBlob> builder)
    {
        builder.ToTable("file_blobs");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(b => b.ContentType).HasMaxLength(255).IsRequired();
        builder.Property(b => b.OriginalName).HasMaxLength(512).IsRequired();
        builder.Property(b => b.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(b => b.ScanStatus).HasMaxLength(32);
        builder.HasIndex(b => b.Sha256);
        builder.HasIndex(b => b.StorageKey).IsUnique();
    }
}

public sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Key).HasMaxLength(128).IsRequired();
        builder.Property(k => k.Operation).HasMaxLength(128).IsRequired();
        builder.Property(k => k.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(k => k.ResponseJson).HasColumnType("jsonb");
        builder.HasIndex(k => new { k.UserId, k.Key }).IsUnique();
        builder.HasIndex(k => k.ExpiresAtUtc);
    }
}
