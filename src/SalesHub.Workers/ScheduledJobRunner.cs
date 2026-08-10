using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// A job this runner can execute. Implementations must be idempotent —
/// a lease lapse after a crash means the job may run again (docs/06).
/// </summary>
public interface IScheduledJobHandler
{
    string JobType { get; }
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Persistent, lease-based scheduled job runner (docs/06). Definitions and
/// state live in scheduled_jobs; every execution writes a scheduled_job_runs
/// row. Cron expressions evaluate in the job's own timezone (normally
/// America/Chicago) and next-run is stored UTC, so a 12:30 AM CT job stays
/// 12:30 AM across DST. Claiming is transactional with a lease, so a crashed
/// host's jobs recover when the lease lapses — never Task.Delay-only state.
/// </summary>
public sealed class ScheduledJobRunner(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IScheduledJobHandler> handlers,
    ILogger<ScheduledJobRunner> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private readonly Dictionary<string, IScheduledJobHandler> _handlers =
        handlers.ToDictionary(h => h.JobType, StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ran = await RunDueJobsAsync(stoppingToken);
                if (ran == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled-job cycle failed; backing off");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    public async Task<int> RunDueJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();

        var claimedIds = await db.Database
            .SqlQuery<Guid>($"""
                UPDATE scheduled_jobs
                SET lease_owner = {_leaseOwner}, lease_expires_at_utc = now() + {LeaseDuration}
                WHERE id IN (
                    SELECT id FROM scheduled_jobs
                    WHERE enabled
                      AND next_run_at_utc IS NOT NULL
                      AND next_run_at_utc <= now()
                      AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc < now())
                    ORDER BY next_run_at_utc
                    LIMIT 5
                    FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """)
            .ToListAsync(ct);

        foreach (var jobId in claimedIds)
        {
            await ExecuteJobAsync(jobId, ct);
        }

        return claimedIds.Count;
    }

    private async Task ExecuteJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();
        var job = await db.ScheduledJobs.FirstAsync(j => j.Id == jobId, ct);

        CorrelationContext.Set(CorrelationContext.NewId());
        var run = new ScheduledJobRun
        {
            Id = Guid.CreateVersion7(),
            JobId = job.Id,
            ScheduledForUtc = job.NextRunAtUtc ?? DateTimeOffset.UtcNow,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Attempt = 1,
            CorrelationId = CorrelationContext.NewId(),
        };
        db.ScheduledJobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            if (!_handlers.TryGetValue(job.JobType, out var handler))
            {
                throw new InvalidOperationException($"No handler for job type '{job.JobType}'.");
            }

            await handler.ExecuteAsync(ct);

            run.Succeeded = true;
            job.LastSuccessAtUtc = DateTimeOffset.UtcNow;
            job.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Succeeded = false;
            run.ErrorClass = ex.GetType().Name;
            run.ErrorMessage = ex.Message;
            job.LastFailureAtUtc = DateTimeOffset.UtcNow;
            job.LastError = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogError(ex, "Scheduled job {JobKey} failed", job.JobKey);
        }
        finally
        {
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            // Advance next-run and release the lease in one committed change;
            // a crash before this point leaves the lease to lapse and re-run.
            job.NextRunAtUtc = ComputeNextRun(job, DateTimeOffset.UtcNow);
            job.LeaseOwner = null;
            job.LeaseExpiresAtUtc = null;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Next occurrence of the job's cron in its own timezone, as UTC.</summary>
    public static DateTimeOffset? ComputeNextRun(ScheduledJob job, DateTimeOffset fromUtc)
    {
        var cron = CronExpression.Parse(job.CronExpression);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(job.TimeZoneId);
        // Cronos yields the occurrence with the zone's local offset; storage
        // is UTC-only (CLAUDE.md §5), so normalize before it reaches the DB.
        return cron.GetNextOccurrence(fromUtc, zone)?.ToUniversalTime();
    }
}
