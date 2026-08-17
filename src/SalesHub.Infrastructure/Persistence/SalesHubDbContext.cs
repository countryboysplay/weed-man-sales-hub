using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Identity;

namespace SalesHub.Infrastructure.Persistence;

/// <summary>
/// The single PostgreSQL DbContext (modular monolith, one schema). Table and
/// column names are snake_case via EFCore.NamingConventions; entity
/// configurations live in <c>Persistence/Configurations</c>, separate from
/// the domain entities (CLAUDE.md §22).
/// </summary>
public class SalesHubDbContext(DbContextOptions<SalesHubDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IAppDb
{
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PublicIdSequence> PublicIdSequences => Set<PublicIdSequence>();
    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();
    public DbSet<ScheduledJobRun> ScheduledJobRuns => Set<ScheduledJobRun>();
    public DbSet<FileBlob> FileBlobs => Set<FileBlob>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<ShiftTemplate> ShiftTemplates => Set<ShiftTemplate>();
    public DbSet<UserShiftAssignment> UserShiftAssignments => Set<UserShiftAssignment>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<PresenceSegment> PresenceSegments => Set<PresenceSegment>();
    public DbSet<PresenceFlag> PresenceFlags => Set<PresenceFlag>();
    public DbSet<PresenceRuleSet> PresenceRuleSets => Set<PresenceRuleSet>();
    public DbSet<TimeOffType> TimeOffTypes => Set<TimeOffType>();
    public DbSet<TimeOffRequest> TimeOffRequests => Set<TimeOffRequest>();
    public DbSet<TimeOffCancellationRequest> TimeOffCancellationRequests => Set<TimeOffCancellationRequest>();
    public DbSet<CoverageRule> CoverageRules => Set<CoverageRule>();
    public DbSet<BreakType> BreakTypes => Set<BreakType>();
    public DbSet<BreakSession> BreakSessions => Set<BreakSession>();
    public DbSet<BreakCorrectionRequest> BreakCorrectionRequests => Set<BreakCorrectionRequest>();
    public DbSet<TechnicalReport> TechnicalReports => Set<TechnicalReport>();
    public DbSet<TechnicalGrant> TechnicalGrants => Set<TechnicalGrant>();
    public DbSet<ManagementNote> ManagementNotes => Set<ManagementNote>();
    public DbSet<ManagementNoteFollowup> ManagementNoteFollowups => Set<ManagementNoteFollowup>();
    public DbSet<ManagementNoteAckTarget> ManagementNoteAckTargets => Set<ManagementNoteAckTarget>();
    public DbSet<RecordLink> RecordLinks => Set<RecordLink>();
    public DbSet<ManagementTag> ManagementTags => Set<ManagementTag>();
    public DbSet<TaggedEntity> TaggedEntities => Set<TaggedEntity>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<SupportCollaborator> SupportCollaborators => Set<SupportCollaborator>();
    public DbSet<SupportAttachment> SupportAttachments => Set<SupportAttachment>();
    public DbSet<SupportLink> SupportLinks => Set<SupportLink>();
    public DbSet<SyncAction> SyncActions => Set<SyncAction>();
    public DbSet<RemoteDeviceCommand> RemoteDeviceCommands => Set<RemoteDeviceCommand>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<ArchiveEntry> ArchiveEntries => Set<ArchiveEntry>();
    public DbSet<OwnerSecurityConfig> OwnerSecurityConfigs => Set<OwnerSecurityConfig>();
    public DbSet<OwnerRecoverySecurityEvent> OwnerRecoverySecurityEvents => Set<OwnerRecoverySecurityEvent>();
    public DbSet<PrivateCommunicationAccess> PrivateCommunicationAccesses => Set<PrivateCommunicationAccess>();
    public DbSet<EmergencyAccessSession> EmergencyAccessSessions => Set<EmergencyAccessSession>();
    public DbSet<SensitiveExport> SensitiveExports => Set<SensitiveExport>();
    public DbSet<SensitiveExportAccess> SensitiveExportAccesses => Set<SensitiveExportAccess>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<DeploymentRecord> DeploymentRecords => Set<DeploymentRecord>();
    public DbSet<StagingRecord> StagingRecords => Set<StagingRecord>();
    public DbSet<RollbackRecord> RollbackRecords => Set<RollbackRecord>();
    public DbSet<RecoveryRecord> RecoveryRecords => Set<RecoveryRecord>();
    public DbSet<KnownGoodVersion> KnownGoodVersions => Set<KnownGoodVersion>();
    public DbSet<BlockedRollbackVersion> BlockedRollbackVersions => Set<BlockedRollbackVersion>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<Form> Forms => Set<Form>();
    public DbSet<FormVersion> FormVersions => Set<FormVersion>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<EmailRequest> EmailRequests => Set<EmailRequest>();
    public DbSet<ResourceFolder> ResourceFolders => Set<ResourceFolder>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceFavorite> ResourceFavorites => Set<ResourceFavorite>();
    public DbSet<ResourceDownloadAudit> ResourceDownloadAudits => Set<ResourceDownloadAudit>();
    public DbSet<TaskDefinition> TaskDefinitions => Set<TaskDefinition>();
    public DbSet<TaskInstance> TaskInstances => Set<TaskInstance>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<TaskAttachment> TaskAttachments => Set<TaskAttachment>();
    public DbSet<RecognitionBadge> RecognitionBadges => Set<RecognitionBadge>();
    public DbSet<Recognition> Recognitions => Set<Recognition>();
    public DbSet<RecognitionReaction> RecognitionReactions => Set<RecognitionReaction>();
    public DbSet<RecognitionComment> RecognitionComments => Set<RecognitionComment>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementTarget> AnnouncementTargets => Set<AnnouncementTarget>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleCorrection> SaleCorrections => Set<SaleCorrection>();
    public DbSet<SaleDuplicateOverride> SaleDuplicateOverrides => Set<SaleDuplicateOverride>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<PasswordResetRequest> PasswordResetRequests => Set<PasswordResetRequest>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            // Already inside a transaction (nested use case) — join it.
            await action(cancellationToken);
            return;
        }

        var strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(SalesHubDbContext).Assembly);

        // Identity's defaults are "AspNetUsers" etc.; the schema follows
        // docs/01 naming (users, roles, ...) like every other table.
        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<ApplicationRole>().ToTable("roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("role_claims");
    }
}
