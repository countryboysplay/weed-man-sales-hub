using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

// ── management records ────────────────────────────────────────────────────────

public sealed class ManagementNoteConfiguration : IEntityTypeConfiguration<ManagementNote>
{
    public void Configure(EntityTypeBuilder<ManagementNote> builder)
    {
        builder.ToTable("management_notes");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(n => n.Category).HasMaxLength(32);
        builder.Property(n => n.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.Body).HasMaxLength(8000).IsRequired();
        builder.Property(n => n.ResolutionNote).HasMaxLength(4000);
        builder.HasIndex(n => n.PublicId).IsUnique();
        builder.HasIndex(n => new { n.EmployeeUserId, n.CreatedAtUtc });
        builder.HasIndex(n => n.PinnedRank).HasFilter("pinned_rank IS NOT NULL");
    }
}

public sealed class ManagementNoteFollowupConfiguration
    : IEntityTypeConfiguration<ManagementNoteFollowup>
{
    public void Configure(EntityTypeBuilder<ManagementNoteFollowup> builder)
    {
        builder.ToTable("management_note_followups");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(f => f.Body).HasMaxLength(8000).IsRequired();
        builder.HasIndex(f => new { f.NoteId, f.CreatedAtUtc });
    }
}

public sealed class ManagementNoteAckTargetConfiguration
    : IEntityTypeConfiguration<ManagementNoteAckTarget>
{
    public void Configure(EntityTypeBuilder<ManagementNoteAckTarget> builder)
    {
        builder.ToTable("management_note_ack_targets");
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => new { a.NoteId, a.TargetUserId }).IsUnique();
    }
}

public sealed class RecordLinkConfiguration : IEntityTypeConfiguration<RecordLink>
{
    public void Configure(EntityTypeBuilder<RecordLink> builder)
    {
        builder.ToTable("record_links");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.SourcePublicId).HasMaxLength(24).IsRequired();
        builder.Property(l => l.TargetPublicId).HasMaxLength(24).IsRequired();
        builder.Property(l => l.RemoveReason).HasMaxLength(1024);
        builder.HasIndex(l => l.SourcePublicId);
        builder.HasIndex(l => l.TargetPublicId);
        builder.Ignore(l => l.IsActive);
    }
}

public sealed class ManagementTagConfiguration : IEntityTypeConfiguration<ManagementTag>
{
    public void Configure(EntityTypeBuilder<ManagementTag> builder)
    {
        builder.ToTable("management_tags");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Label).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.Label).IsUnique();
    }
}

public sealed class TaggedEntityConfiguration : IEntityTypeConfiguration<TaggedEntity>
{
    public void Configure(EntityTypeBuilder<TaggedEntity> builder)
    {
        builder.ToTable("tagged_entities");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.EntityPublicId).HasMaxLength(24).IsRequired();
        builder.HasIndex(t => new { t.TagId, t.EntityPublicId }).IsUnique();
        builder.HasIndex(t => t.EntityPublicId);
    }
}

// ── support ───────────────────────────────────────────────────────────────────

public sealed class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> builder)
    {
        builder.ToTable("support_tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(t => t.IssueType).HasMaxLength(64);
        builder.Property(t => t.Description).HasMaxLength(8000).IsRequired();
        builder.Property(t => t.Page).HasMaxLength(256);
        builder.Property(t => t.AppVersion).HasMaxLength(64);
        builder.Property(t => t.BrowserFamily).HasMaxLength(128);
        builder.Property(t => t.DeviceId).HasMaxLength(128);
        builder.Property(t => t.CorrelationId).HasMaxLength(64);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.SuggestedPriority).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.SuggestedPriorityReason).HasMaxLength(256);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(t => t.PublicId).IsUnique();
        builder.HasIndex(t => new { t.ReporterUserId, t.CreatedAtUtc });
        builder.HasIndex(t => new { t.Status, t.Priority });
    }
}

public sealed class SupportMessageConfiguration : IEntityTypeConfiguration<SupportMessage>
{
    public void Configure(EntityTypeBuilder<SupportMessage> builder)
    {
        builder.ToTable("support_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Visibility).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Body).HasMaxLength(8000).IsRequired();
        builder.HasIndex(m => new { m.TicketId, m.CreatedAtUtc });
    }
}

public sealed class SupportCollaboratorConfiguration : IEntityTypeConfiguration<SupportCollaborator>
{
    public void Configure(EntityTypeBuilder<SupportCollaborator> builder)
    {
        builder.ToTable("support_collaborators");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.TicketId, c.UserId }).IsUnique();
    }
}

public sealed class SupportAttachmentConfiguration : IEntityTypeConfiguration<SupportAttachment>
{
    public void Configure(EntityTypeBuilder<SupportAttachment> builder)
    {
        builder.ToTable("support_attachments");
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => a.TicketId);
    }
}

public sealed class SupportLinkConfiguration : IEntityTypeConfiguration<SupportLink>
{
    public void Configure(EntityTypeBuilder<SupportLink> builder)
    {
        builder.ToTable("support_links");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.TargetPublicId).HasMaxLength(24).IsRequired();
        builder.HasIndex(l => new { l.TicketId, l.TargetPublicId }).IsUnique();
    }
}

// ── sync / system ops ─────────────────────────────────────────────────────────

public sealed class SyncActionConfiguration : IEntityTypeConfiguration<SyncAction>
{
    public void Configure(EntityTypeBuilder<SyncAction> builder)
    {
        builder.ToTable("sync_actions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.DeviceId).HasMaxLength(128);
        builder.Property(s => s.Operation).HasMaxLength(128).IsRequired();
        builder.Property(s => s.IdempotencyKey).HasMaxLength(128);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Error).HasMaxLength(1024);
        builder.HasIndex(s => new { s.UserId, s.CreatedAtUtc });
        builder.HasIndex(s => s.CreatedAtUtc);
    }
}

public sealed class RemoteDeviceCommandConfiguration
    : IEntityTypeConfiguration<RemoteDeviceCommand>
{
    public void Configure(EntityTypeBuilder<RemoteDeviceCommand> builder)
    {
        builder.ToTable("remote_device_commands");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CommandType).HasConversion<string>().HasMaxLength(24);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.TargetDeviceId).HasMaxLength(128);
        builder.HasIndex(c => new { c.TargetUserId, c.Status });
        builder.HasIndex(c => c.CreatedAtUtc);
    }
}

public sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> builder)
    {
        builder.ToTable("report_schedules");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ReportType).HasConversion<string>().HasMaxLength(40);
        builder.Property(s => s.Cadence).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(s => s.NextDueAtUtc).HasFilter("enabled");
    }
}

public sealed class ReportRunConfiguration : IEntityTypeConfiguration<ReportRun>
{
    public void Configure(EntityTypeBuilder<ReportRun> builder)
    {
        builder.ToTable("report_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ReportType).HasConversion<string>().HasMaxLength(40);
        builder.Property(r => r.Error).HasMaxLength(1024);
        builder.HasIndex(r => new { r.ReportType, r.StartedAtUtc });
    }
}

public sealed class ArchiveEntryConfiguration : IEntityTypeConfiguration<ArchiveEntry>
{
    public void Configure(EntityTypeBuilder<ArchiveEntry> builder)
    {
        builder.ToTable("archive_entries");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Title).HasMaxLength(256).IsRequired();
        builder.Property(a => a.ReportType).HasConversion<string>().HasMaxLength(40);
        builder.Property(a => a.RecoveredFromNote).HasMaxLength(512);
        builder.HasIndex(a => a.CreatedAtUtc);
    }
}
