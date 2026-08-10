using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Domain.Entities;
using SalesHub.TestSupport;
using SalesHub.Workers;
using SalesHub.Workers.Jobs;
using Xunit;

namespace SalesHub.IntegrationTests;

/// <summary>
/// Transactional outbox and persistent scheduled jobs on real PostgreSQL:
/// claim semantics, retry/poison behavior, lease-based job execution.
/// Workers are disabled in this factory; dispatch is driven by hand.
/// </summary>
public class OutboxAndJobsTests : IAsyncLifetime
{
    private SalesHubApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SalesHubApiFactory();
        _ = _factory.Services; // force host start
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>Publisher stand-in recording deliveries, optionally failing.</summary>
    private sealed class RecordingPublisher : IRealtimePublisher
    {
        public List<(string Target, EventEnvelope Envelope)> Delivered { get; } = [];
        public int FailuresRemaining { get; set; }

        public Task PublishToUserAsync(Guid userId, EventEnvelope envelope, CancellationToken ct = default) =>
            Record($"user:{userId}", envelope);

        public Task PublishToGroupAsync(string group, EventEnvelope envelope, CancellationToken ct = default) =>
            Record(group, envelope);

        private Task Record(string target, EventEnvelope envelope)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("simulated delivery failure");
            }

            Delivered.Add((target, envelope));
            return Task.CompletedTask;
        }
    }

    private OutboxDispatcher DispatcherWith(RecordingPublisher publisher)
    {
        // A dispatcher wired to the same database but a controllable publisher.
        var provider = new ServiceCollection()
            .AddSingleton<IRealtimePublisher>(publisher)
            .AddDbContext<SalesHub.Infrastructure.Persistence.SalesHubDbContext>(options =>
                options.UseNpgsql(_factory.ConnectionString).UseSnakeCaseNamingConvention())
            .BuildServiceProvider();
        return new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            sideEffects: [],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxDispatcher>.Instance);
    }

    private async Task EnqueueAsync(string eventType, object payload)
    {
        await _factory.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<IAppDb>();
            var outbox = sp.GetRequiredService<IOutboxWriter>();
            await outbox.EnqueueAsync(eventType, payload);
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task A_committed_outbox_row_is_delivered_exactly_once_and_marked_processed()
    {
        var userId = Guid.NewGuid();
        await EnqueueAsync("system.ping.v1", new { userId, hello = "world" });

        var publisher = new RecordingPublisher();
        var dispatcher = DispatcherWith(publisher);

        var first = await dispatcher.DispatchBatchAsync(CancellationToken.None);
        Assert.Equal(1, first);
        var again = await dispatcher.DispatchBatchAsync(CancellationToken.None);
        Assert.Equal(0, again); // processed rows are never re-claimed

        var delivery = Assert.Single(publisher.Delivered);
        Assert.Equal($"user:{userId}", delivery.Target);
        Assert.Equal("system.ping.v1", delivery.Envelope.EventType);

        var row = await _factory.WithDbAsync(db => db.OutboxMessages.SingleAsync());
        Assert.NotNull(row.ProcessedAtUtc);
        Assert.False(row.Failed);
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_after_backoff_not_lost()
    {
        await EnqueueAsync("system.ping.v1", new { data = 1 });

        var publisher = new RecordingPublisher { FailuresRemaining = 1 };
        var dispatcher = DispatcherWith(publisher);

        await dispatcher.DispatchBatchAsync(CancellationToken.None);
        var afterFailure = await _factory.WithDbAsync(db => db.OutboxMessages.SingleAsync());
        Assert.Null(afterFailure.ProcessedAtUtc);
        Assert.False(afterFailure.Failed);
        Assert.Contains("simulated delivery failure", afterFailure.LastError);
        Assert.True(afterFailure.AvailableAtUtc > DateTimeOffset.UtcNow); // backed off

        // Make it due again and confirm the retry delivers.
        await _factory.WithDbAsync(db => db.OutboxMessages
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AvailableAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1))));
        await dispatcher.DispatchBatchAsync(CancellationToken.None);

        var delivered = await _factory.WithDbAsync(db => db.OutboxMessages.SingleAsync());
        Assert.NotNull(delivered.ProcessedAtUtc);
        Assert.Single(publisher.Delivered);
    }

    [Fact]
    public async Task A_poison_event_is_parked_as_failed_not_deleted()
    {
        await EnqueueAsync("system.ping.v1", new { data = "poison" });
        // Push attempts to the budget's edge, as if it failed repeatedly.
        await _factory.WithDbAsync(db => db.OutboxMessages
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, 8)));

        var publisher = new RecordingPublisher { FailuresRemaining = 99 };
        await DispatcherWith(publisher).DispatchBatchAsync(CancellationToken.None);

        var row = await _factory.WithDbAsync(db => db.OutboxMessages.SingleAsync());
        Assert.True(row.Failed);
        Assert.Null(row.ProcessedAtUtc); // parked, never deleted
    }

    [Fact]
    public async Task Public_id_sequences_allocate_uniquely_under_concurrency()
    {
        var allocations = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(async () =>
            {
                using var scope = _factory.Services.CreateScope();
                var generator = scope.ServiceProvider.GetRequiredService<IPublicIdGenerator>();
                return await generator.NextAsync("NOTE");
            })));

        Assert.Equal(20, allocations.Distinct().Count());
        Assert.All(allocations, id => Assert.Matches(@"^NOTE-\d{4}-\d{5}$", id));

        // Different prefixes count independently.
        using var scope = _factory.Services.CreateScope();
        var gen = scope.ServiceProvider.GetRequiredService<IPublicIdGenerator>();
        Assert.EndsWith("-00001", await gen.NextAsync("TECH"));
    }

    [Fact]
    public async Task Due_scheduled_jobs_run_record_their_outcome_and_advance()
    {
        // Make the seeded stale-scan job due now.
        await _factory.WithDbAsync(db => db.ScheduledJobs
            .Where(j => j.JobKey == IdleCapabilityStaleScanJob.Type)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.NextRunAtUtc, DateTimeOffset.UtcNow.AddSeconds(-5))
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTimeOffset?)null)));

        var runner = _factory.Services.GetRequiredService<ScheduledJobRunner>();
        var ran = await runner.RunDueJobsAsync(CancellationToken.None);
        Assert.True(ran >= 1);

        var job = await _factory.WithDbAsync(db =>
            db.ScheduledJobs.SingleAsync(j => j.JobKey == IdleCapabilityStaleScanJob.Type));
        Assert.NotNull(job.LastSuccessAtUtc);
        Assert.Null(job.LeaseOwner);                       // lease released
        Assert.True(job.NextRunAtUtc > DateTimeOffset.UtcNow); // advanced

        var run = await _factory.WithDbAsync(db =>
            db.ScheduledJobRuns.SingleAsync(r => r.JobId == job.Id));
        Assert.True(run.Succeeded);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task A_leased_job_is_not_claimed_by_another_runner()
    {
        await _factory.WithDbAsync(db => db.ScheduledJobs
            .Where(j => j.JobKey == IdempotencyKeyCleanupJob.Type)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.NextRunAtUtc, DateTimeOffset.UtcNow.AddSeconds(-5))
                .SetProperty(j => j.LeaseOwner, "another-host:123")
                .SetProperty(j => j.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(4))));

        var runner = _factory.Services.GetRequiredService<ScheduledJobRunner>();
        _ = await runner.RunDueJobsAsync(CancellationToken.None);

        var job = await _factory.WithDbAsync(db =>
            db.ScheduledJobs.SingleAsync(j => j.JobKey == IdempotencyKeyCleanupJob.Type));
        Assert.Equal("another-host:123", job.LeaseOwner); // untouched while leased
    }

    [Fact]
    public async Task An_expired_lease_is_recovered_by_the_next_runner()
    {
        await _factory.WithDbAsync(db => db.ScheduledJobs
            .Where(j => j.JobKey == IdempotencyKeyCleanupJob.Type)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.NextRunAtUtc, DateTimeOffset.UtcNow.AddMinutes(-10))
                .SetProperty(j => j.LeaseOwner, "crashed-host:999")
                .SetProperty(j => j.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1))));

        var runner = _factory.Services.GetRequiredService<ScheduledJobRunner>();
        var ran = await runner.RunDueJobsAsync(CancellationToken.None);
        Assert.True(ran >= 1);

        var job = await _factory.WithDbAsync(db =>
            db.ScheduledJobs.SingleAsync(j => j.JobKey == IdempotencyKeyCleanupJob.Type));
        Assert.NotNull(job.LastSuccessAtUtc);
    }

    [Fact]
    public async Task Idempotency_keys_are_unique_per_user_and_key()
    {
        await _factory.WithDbAsync(async db =>
        {
            var userId = Guid.NewGuid();
            db.IdempotencyKeys.Add(NewKey(userId, "abc"));
            await db.SaveChangesAsync();

            db.IdempotencyKeys.Add(NewKey(userId, "abc"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            return 0;
        });
    }

    private static IdempotencyKey NewKey(Guid userId, string key) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Key = key,
        Operation = "test",
        RequestHash = "hash",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
    };
}
