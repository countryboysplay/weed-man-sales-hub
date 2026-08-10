using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SalesHub.Application.Abstractions;
using WebPush;
using DomainPushSubscription = SalesHub.Domain.Entities.PushSubscription;

namespace SalesHub.Infrastructure.Services;

public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public string Subject { get; set; } = "";      // mailto: or https: origin
    public string VapidPublicKey { get; set; } = "";
    public string VapidPrivateKey { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Subject)
        && !string.IsNullOrWhiteSpace(VapidPublicKey)
        && !string.IsNullOrWhiteSpace(VapidPrivateKey);
}

/// <summary>
/// Standard Web Push with VAPID (docs/03). Unconfigured deployments are a
/// clean no-op — the Notification Center rows remain the source of truth,
/// push is only a delivery channel. The private key is never logged.
/// </summary>
public sealed class VapidWebPushSender : IWebPushSender, IDisposable
{
    private readonly WebPushClient _client = new();
    private readonly WebPushOptions _options;
    private readonly ILogger<VapidWebPushSender> _logger;

    public VapidWebPushSender(IOptions<WebPushOptions> options, ILogger<VapidWebPushSender> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "Web Push is disabled: WebPush:Subject/VapidPublicKey/VapidPrivateKey not configured");
        }
    }

    public bool Enabled => _options.IsConfigured;

    public async Task<PushSendResult> SendAsync(
        DomainPushSubscription subscription, string payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return PushSendResult.Disabled;
        }

        try
        {
            await _client.SendNotificationAsync(
                new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth),
                payloadJson,
                new VapidDetails(_options.Subject, _options.VapidPublicKey, _options.VapidPrivateKey),
                cancellationToken);
            return PushSendResult.Delivered;
        }
        catch (WebPushException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return PushSendResult.Gone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Web Push delivery failed for subscription {SubscriptionId}",
                subscription.Id);
            return PushSendResult.Failed;
        }
    }

    public void Dispose() => _client.Dispose();
}
