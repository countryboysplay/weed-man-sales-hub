using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class OwnerSecurityConfigConfiguration : IEntityTypeConfiguration<OwnerSecurityConfig>
{
    public void Configure(EntityTypeBuilder<OwnerSecurityConfig> builder)
    {
        builder.ToTable("owner_security_configs");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.MasterCredentialHash).HasMaxLength(512).IsRequired();
        builder.Property(c => c.TotpSecretEncrypted).HasMaxLength(1024);
        builder.HasIndex(c => c.OwnerUserId).IsUnique();
    }
}

public sealed class OwnerRecoverySecurityEventConfiguration
    : IEntityTypeConfiguration<OwnerRecoverySecurityEvent>
{
    public void Configure(EntityTypeBuilder<OwnerRecoverySecurityEvent> builder)
    {
        builder.ToTable("owner_recovery_security_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(1024);
        builder.HasIndex(e => e.OccurredAtUtc);
    }
}

public sealed class PrivateCommunicationAccessConfiguration
    : IEntityTypeConfiguration<PrivateCommunicationAccess>
{
    public void Configure(EntityTypeBuilder<PrivateCommunicationAccess> builder)
    {
        builder.ToTable("private_communication_access");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Scope).HasMaxLength(64);
        builder.Property(a => a.TargetConversationIdsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(a => a.AccessSessionId).IsUnique();
        builder.HasIndex(a => a.OwnerUserId);
    }
}

public sealed class EmergencyAccessSessionConfiguration
    : IEntityTypeConfiguration<EmergencyAccessSession>
{
    public void Configure(EntityTypeBuilder<EmergencyAccessSession> builder)
    {
        builder.ToTable("emergency_access_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Reason).HasMaxLength(1024).IsRequired();
        builder.Property(s => s.EndReason).HasMaxLength(1024);
        builder.HasIndex(s => new { s.OwnerUserId, s.StartedAtUtc });
    }
}

public sealed class SensitiveExportConfiguration : IEntityTypeConfiguration<SensitiveExport>
{
    public void Configure(EntityTypeBuilder<SensitiveExport> builder)
    {
        builder.ToTable("sensitive_exports");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Format).HasMaxLength(8).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(e => e.PublicId).IsUnique();
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}

public sealed class SensitiveExportAccessConfiguration
    : IEntityTypeConfiguration<SensitiveExportAccess>
{
    public void Configure(EntityTypeBuilder<SensitiveExportAccess> builder)
    {
        builder.ToTable("sensitive_export_access");
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => new { a.ExportId, a.AccessedAtUtc });
    }
}

public sealed class SettingEntryConfiguration : IEntityTypeConfiguration<SettingEntry>
{
    public void Configure(EntityTypeBuilder<SettingEntry> builder)
    {
        builder.ToTable("settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Key).HasMaxLength(128).IsRequired();
        builder.Property(s => s.ValueJson).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.Scope).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(s => s.Key).IsUnique();
    }
}

public sealed class DeploymentRecordConfiguration : IEntityTypeConfiguration<DeploymentRecord>
{
    public void Configure(EntityTypeBuilder<DeploymentRecord> builder)
    {
        builder.ToTable("deployment_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(r => r.Version).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(2048);
        builder.HasIndex(r => r.PublicId).IsUnique();
    }
}

public sealed class StagingRecordConfiguration : IEntityTypeConfiguration<StagingRecord>
{
    public void Configure(EntityTypeBuilder<StagingRecord> builder)
    {
        builder.ToTable("staging_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(r => r.PublicId).IsUnique();
    }
}

public sealed class RollbackRecordConfiguration : IEntityTypeConfiguration<RollbackRecord>
{
    public void Configure(EntityTypeBuilder<RollbackRecord> builder)
    {
        builder.ToTable("rollback_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(r => r.FromVersion).HasMaxLength(64);
        builder.Property(r => r.ToVersion).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(r => r.PublicId).IsUnique();
    }
}

public sealed class RecoveryRecordConfiguration : IEntityTypeConfiguration<RecoveryRecord>
{
    public void Configure(EntityTypeBuilder<RecoveryRecord> builder)
    {
        builder.ToTable("recovery_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(r => r.SourceDescription).HasMaxLength(512);
        builder.Property(r => r.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(r => r.PublicId).IsUnique();
    }
}

public sealed class KnownGoodVersionConfiguration : IEntityTypeConfiguration<KnownGoodVersion>
{
    public void Configure(EntityTypeBuilder<KnownGoodVersion> builder)
    {
        builder.ToTable("known_good_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Version).HasMaxLength(64).IsRequired();
        builder.HasIndex(v => v.Version).IsUnique();
    }
}

public sealed class BlockedRollbackVersionConfiguration
    : IEntityTypeConfiguration<BlockedRollbackVersion>
{
    public void Configure(EntityTypeBuilder<BlockedRollbackVersion> builder)
    {
        builder.ToTable("blocked_rollback_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Version).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(v => v.Version).IsUnique();
    }
}

public sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("maintenance_windows");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(w => w.StartAtUtc);
    }
}
