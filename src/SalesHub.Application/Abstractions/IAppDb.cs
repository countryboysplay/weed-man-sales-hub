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
