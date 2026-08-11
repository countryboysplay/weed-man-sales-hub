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
