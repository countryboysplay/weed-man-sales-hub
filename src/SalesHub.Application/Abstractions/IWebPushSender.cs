using SalesHub.Domain.Entities;

namespace SalesHub.Application.Abstractions;

/// <summary>
/// Web Push delivery port (docs/03). Payloads must stay minimal and
/// lock-screen safe. Implementations return Gone when the platform reports
/// the subscription dead so the caller can deactivate it.
/// </summary>
public interface IWebPushSender
{
    bool Enabled { get; }

    Task<PushSendResult> SendAsync(
        PushSubscription subscription, string payloadJson,
        CancellationToken cancellationToken = default);
}

public enum PushSendResult
{
    Delivered = 0,
    Gone = 1,       // 404/410 — subscription expired or unsubscribed
    Failed = 2,
    Disabled = 3,   // no VAPID configuration
}
