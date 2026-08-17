using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SalesHub.Domain.Entities;

namespace SalesHub.Infrastructure.Persistence.Configurations;

public sealed class ShiftTemplateConfiguration : IEntityTypeConfiguration<ShiftTemplate>
{
    public void Configure(EntityTypeBuilder<ShiftTemplate> builder)
    {
        builder.ToTable("shift_templates");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(128).IsRequired();
        builder.Property(s => s.Role).HasMaxLength(32);
        builder.Property(s => s.DayOfWeek).HasConversion<string>().HasMaxLength(16);
    }
}

public sealed class UserShiftAssignmentConfiguration : IEntityTypeConfiguration<UserShiftAssignment>
{
    public void Configure(EntityTypeBuilder<UserShiftAssignment> builder)
    {
        builder.ToTable("user_shift_assignments");
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => new { a.UserId, a.StartDate });
    }
}

public sealed class ScheduleExceptionConfiguration : IEntityTypeConfiguration<ScheduleException>
{
    public void Configure(EntityTypeBuilder<ScheduleException> builder)
    {
        builder.ToTable("schedule_exceptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(s => s.Label).HasMaxLength(128);
        builder.Property(s => s.Reason).HasMaxLength(1024);
        builder.HasIndex(s => s.PublicId).IsUnique();
        builder.HasIndex(s => new { s.UserId, s.Date });
    }
}

public sealed class PresenceSegmentConfiguration : IEntityTypeConfiguration<PresenceSegment>
{
    public void Configure(EntityTypeBuilder<PresenceSegment> builder)
    {
        builder.ToTable("presence_segments");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(s => new { s.UserId, s.StartAtUtc });
        // The evaluator closes the open segment per user.
        builder.HasIndex(s => s.UserId).HasFilter("end_at_utc IS NULL")
            .HasDatabaseName("ix_presence_segments_open");
    }
}

public sealed class PresenceFlagConfiguration : IEntityTypeConfiguration<PresenceFlag>
{
    public void Configure(EntityTypeBuilder<PresenceFlag> builder)
    {
        builder.ToTable("presence_flags");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(f => f.Category).HasMaxLength(32);
        builder.Property(f => f.Severity).HasConversion<string>().HasMaxLength(16);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(f => f.Source).HasMaxLength(128);
        builder.Property(f => f.LinkedPublicIds).HasMaxLength(256);
        builder.HasIndex(f => f.PublicId).IsUnique();
        builder.HasIndex(f => new { f.UserId, f.BusinessDate });
        // One open flag per user+category+day keeps the evaluator idempotent.
        builder.HasIndex(f => new { f.UserId, f.Category, f.BusinessDate }).IsUnique();
    }
}

public sealed class PresenceRuleSetConfiguration : IEntityTypeConfiguration<PresenceRuleSet>
{
    public void Configure(EntityTypeBuilder<PresenceRuleSet> builder)
    {
        builder.ToTable("presence_rule_sets");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Role).HasMaxLength(32);
        builder.HasIndex(r => r.Role).IsUnique();
    }
}

public sealed class TimeOffTypeConfiguration : IEntityTypeConfiguration<TimeOffType>
{
    public void Configure(EntityTypeBuilder<TimeOffType> builder)
    {
        builder.ToTable("time_off_types");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Label).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.Label).IsUnique();
    }
}

public sealed class TimeOffRequestConfiguration : IEntityTypeConfiguration<TimeOffRequest>
{
    public void Configure(EntityTypeBuilder<TimeOffRequest> builder)
    {
        builder.ToTable("time_off_requests");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Reason).HasMaxLength(1024);
        builder.Property(t => t.ReviewNote).HasMaxLength(1024);
        builder.Property(t => t.DenialReason).HasMaxLength(1024);
        builder.Property(t => t.CoverageSnapshotJson).HasColumnType("jsonb");
        builder.HasIndex(t => t.PublicId).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.Status });
        builder.HasIndex(t => new { t.Status, t.StartDate });
    }
}

public sealed class TimeOffCancellationRequestConfiguration
    : IEntityTypeConfiguration<TimeOffCancellationRequest>
{
    public void Configure(EntityTypeBuilder<TimeOffCancellationRequest> builder)
    {
        builder.ToTable("time_off_cancellation_requests");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ResultStatus).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(c => c.TimeOffRequestId);
    }
}

public sealed class CoverageRuleConfiguration : IEntityTypeConfiguration<CoverageRule>
{
    public void Configure(EntityTypeBuilder<CoverageRule> builder)
    {
        builder.ToTable("coverage_rules");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Role).HasMaxLength(32);
        builder.Property(c => c.Behavior).HasConversion<string>().HasMaxLength(24);
        builder.HasIndex(c => c.Role).IsUnique();
    }
}

public sealed class BreakTypeConfiguration : IEntityTypeConfiguration<BreakType>
{
    public void Configure(EntityTypeBuilder<BreakType> builder)
    {
        builder.ToTable("break_types");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Label).HasMaxLength(64).IsRequired();
        builder.HasIndex(b => b.Label).IsUnique();
    }
}

public sealed class BreakSessionConfiguration : IEntityTypeConfiguration<BreakSession>
{
    public void Configure(EntityTypeBuilder<BreakSession> builder)
    {
        builder.ToTable("break_sessions");
        builder.HasKey(b => b.Id);
        builder.HasIndex(b => new { b.UserId, b.BusinessDate });
        // One active break per user, enforced in the database too.
        builder.HasIndex(b => b.UserId).HasFilter("ended_at_utc IS NULL").IsUnique()
            .HasDatabaseName("ux_break_sessions_one_active");
    }
}

public sealed class BreakCorrectionRequestConfiguration
    : IEntityTypeConfiguration<BreakCorrectionRequest>
{
    public void Configure(EntityTypeBuilder<BreakCorrectionRequest> builder)
    {
        builder.ToTable("break_correction_requests");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(b => b.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(b => b.PublicId).IsUnique();
        builder.HasIndex(b => b.Status);
    }
}

public sealed class TechnicalReportConfiguration : IEntityTypeConfiguration<TechnicalReport>
{
    public void Configure(EntityTypeBuilder<TechnicalReport> builder)
    {
        builder.ToTable("technical_reports");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.PublicId).HasMaxLength(24).IsRequired();
        builder.Property(t => t.IssueType).HasMaxLength(32);
        builder.Property(t => t.Description).HasMaxLength(4000);
        builder.Property(t => t.Page).HasMaxLength(256);
        builder.Property(t => t.AppVersion).HasMaxLength(64);
        builder.Property(t => t.BrowserFamily).HasMaxLength(128);
        builder.HasIndex(t => t.PublicId).IsUnique();
    }
}

public sealed class TechnicalGrantConfiguration : IEntityTypeConfiguration<TechnicalGrant>
{
    public void Configure(EntityTypeBuilder<TechnicalGrant> builder)
    {
        builder.ToTable("technical_grants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Reason).HasMaxLength(1024).IsRequired();
        builder.HasIndex(t => new { t.UserId, t.StartAtUtc });
    }
}
