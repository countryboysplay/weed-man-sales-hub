using Microsoft.EntityFrameworkCore;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Abstractions;

/// <summary>
/// The application's view of the database. Implemented by the Infrastructure
/// DbContext; use cases query and mutate through this so business logic never
/// references the concrete context or its EF configurations.
/// </summary>
public interface IAppDb
{
    DbSet<UserSession> UserSessions { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<PublicIdSequence> PublicIdSequences { get; }
    DbSet<ScheduledJob> ScheduledJobs { get; }
    DbSet<ScheduledJobRun> ScheduledJobRuns { get; }
    DbSet<FileBlob> FileBlobs { get; }
    DbSet<IdempotencyKey> IdempotencyKeys { get; }
    DbSet<ShiftTemplate> ShiftTemplates { get; }
    DbSet<UserShiftAssignment> UserShiftAssignments { get; }
    DbSet<ScheduleException> ScheduleExceptions { get; }
    DbSet<PresenceSegment> PresenceSegments { get; }
    DbSet<PresenceFlag> PresenceFlags { get; }
    DbSet<PresenceRuleSet> PresenceRuleSets { get; }
    DbSet<TimeOffType> TimeOffTypes { get; }
    DbSet<TimeOffRequest> TimeOffRequests { get; }
    DbSet<TimeOffCancellationRequest> TimeOffCancellationRequests { get; }
    DbSet<CoverageRule> CoverageRules { get; }
    DbSet<BreakType> BreakTypes { get; }
    DbSet<BreakSession> BreakSessions { get; }
    DbSet<BreakCorrectionRequest> BreakCorrectionRequests { get; }
    DbSet<TechnicalReport> TechnicalReports { get; }
    DbSet<TechnicalGrant> TechnicalGrants { get; }
    DbSet<ManagementNote> ManagementNotes { get; }
    DbSet<ManagementNoteFollowup> ManagementNoteFollowups { get; }
    DbSet<ManagementNoteAckTarget> ManagementNoteAckTargets { get; }
    DbSet<RecordLink> RecordLinks { get; }
    DbSet<ManagementTag> ManagementTags { get; }
    DbSet<TaggedEntity> TaggedEntities { get; }
    DbSet<SupportTicket> SupportTickets { get; }
    DbSet<SupportMessage> SupportMessages { get; }
    DbSet<SupportCollaborator> SupportCollaborators { get; }
    DbSet<SupportAttachment> SupportAttachments { get; }
    DbSet<SupportLink> SupportLinks { get; }
    DbSet<SyncAction> SyncActions { get; }
    DbSet<RemoteDeviceCommand> RemoteDeviceCommands { get; }
    DbSet<ReportSchedule> ReportSchedules { get; }
    DbSet<ReportRun> ReportRuns { get; }
    DbSet<ArchiveEntry> ArchiveEntries { get; }
    DbSet<Form> Forms { get; }
    DbSet<FormVersion> FormVersions { get; }
    DbSet<FormSubmission> FormSubmissions { get; }
    DbSet<EmailRequest> EmailRequests { get; }
    DbSet<ResourceFolder> ResourceFolders { get; }
    DbSet<Resource> Resources { get; }
    DbSet<ResourceFavorite> ResourceFavorites { get; }
    DbSet<ResourceDownloadAudit> ResourceDownloadAudits { get; }
    DbSet<TaskDefinition> TaskDefinitions { get; }
    DbSet<TaskInstance> TaskInstances { get; }
    DbSet<TaskComment> TaskComments { get; }
    DbSet<TaskAttachment> TaskAttachments { get; }
    DbSet<RecognitionBadge> RecognitionBadges { get; }
    DbSet<Recognition> Recognitions { get; }
    DbSet<RecognitionReaction> RecognitionReactions { get; }
    DbSet<RecognitionComment> RecognitionComments { get; }
    DbSet<Announcement> Announcements { get; }
    DbSet<AnnouncementTarget> AnnouncementTargets { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationMember> ConversationMembers { get; }
    DbSet<Message> Messages { get; }
    DbSet<MessageAttachment> MessageAttachments { get; }
    DbSet<MessageReaction> MessageReactions { get; }
    DbSet<Sale> Sales { get; }
    DbSet<SaleCorrection> SaleCorrections { get; }
    DbSet<SaleDuplicateOverride> SaleDuplicateOverrides { get; }
    DbSet<Branch> Branches { get; }
    DbSet<PasswordResetRequest> PasswordResetRequests { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<NotificationDelivery> NotificationDeliveries { get; }
    DbSet<PushSubscription> PushSubscriptions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the delegate inside a single database transaction. Use for every
    /// multi-step business change (state + audit + outbox commit together).
    /// </summary>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}
