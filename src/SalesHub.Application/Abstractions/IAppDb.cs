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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the delegate inside a single database transaction. Use for every
    /// multi-step business change (state + audit + outbox commit together).
    /// </summary>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}
