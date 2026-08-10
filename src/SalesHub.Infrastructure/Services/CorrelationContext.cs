using SalesHub.Application.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Ambient correlation id. The API middleware sets it per request; workers
/// set it per outbox batch / job run, so every audit row and log line can be
/// traced to its origin.
/// </summary>
public sealed class CorrelationContext : ICorrelationAccessor
{
    private static readonly AsyncLocal<string?> Current = new();

    public string CorrelationId =>
        Current.Value ??= $"WM-{Guid.CreateVersion7():N}"[..15].ToUpperInvariant();

    public static void Set(string correlationId) => Current.Value = correlationId;

    public static string NewId() => $"WM-{Guid.NewGuid():N}"[..15].ToUpperInvariant();
}
