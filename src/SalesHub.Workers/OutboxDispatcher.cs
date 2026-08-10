using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Infrastructure.Persistence;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Workers;

/// <summary>
/// Delivers committed outbox rows to SignalR (docs/03). Claims batches with
/// FOR UPDATE SKIP LOCKED so multiple hosts never double-deliver; failures
/// back off and, past the attempt budget, park the row as failed for the
/// health surface. Rows are never deleted here — retention jobs own that.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);
    private const int BatchSize = 25;
    private const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delivered = await DispatchBatchAsync(stoppingToken);
                if (delivered == 0)
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
                logger.LogError(ex, "Outbox dispatch cycle failed; backing off");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesHubDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IRealtimePublisher>();

        var claimed = await db.Database
            .SqlQuery<ClaimedMessage>($"""
                UPDATE outbox_messages
                SET claimed_at_utc = now(), attempts = attempts + 1
                WHERE id IN (
                    SELECT id FROM outbox_messages
                    WHERE processed_at_utc IS NULL
                      AND NOT failed
                      AND available_at_utc <= now()
                      AND (claimed_at_utc IS NULL OR claimed_at_utc < now() - {ClaimTimeout})
                    ORDER BY available_at_utc
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING id, event_type, payload_json, occurred_at_utc, correlation_id, attempts
                """)
            .ToListAsync(ct);

        foreach (var message in claimed)
        {
            CorrelationContext.Set(message.CorrelationId);
            try
            {
                await DeliverAsync(publisher, message, ct);
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE outbox_messages SET processed_at_utc = now(), last_error = NULL WHERE id = {message.Id}",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failedForGood = message.Attempts >= MaxAttempts;
                var backoff = TimeSpan.FromSeconds(Math.Min(600, Math.Pow(2, message.Attempts) * 5));
                logger.LogWarning(ex,
                    "Outbox delivery failed for {EventType} {MessageId}, attempt {Attempt}{Parked}",
                    message.EventType, message.Id, message.Attempts,
                    failedForGood ? " — parked as failed" : "");
                await db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE outbox_messages
                     SET last_error = {ex.GetType().Name + ": " + ex.Message},
                         failed = {failedForGood},
                         claimed_at_utc = NULL,
                         available_at_utc = now() + {backoff}
                     WHERE id = {message.Id}
                     """,
                    ct);
            }
        }

        return claimed.Count;
    }

    private static async Task DeliverAsync(
        IRealtimePublisher publisher, ClaimedMessage message, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var envelope = new EventEnvelope(
            message.Id,
            message.EventType,
            message.OccurredAtUtc,
            message.CorrelationId,
            payload.RootElement.Clone());

        // Routing (docs/03): events carrying a userId go to that user's group;
        // everything else goes to management. Later waves extend this map
        // (conversation:, role:, branch: targets) alongside their modules.
        if (payload.RootElement.TryGetProperty("userId", out var userIdProperty)
            && userIdProperty.TryGetGuid(out var userId))
        {
            await publisher.PublishToUserAsync(userId, envelope, ct);
        }
        else
        {
            await publisher.PublishToGroupAsync("management", envelope, ct);
        }
    }

    public sealed record ClaimedMessage(
        Guid Id,
        string EventType,
        string PayloadJson,
        DateTimeOffset OccurredAtUtc,
        string CorrelationId,
        int Attempts);
}
