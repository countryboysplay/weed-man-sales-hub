namespace SalesHub.Application.Abstractions;

/// <summary>The correlation id of the current request/job execution.</summary>
public interface ICorrelationAccessor
{
    string CorrelationId { get; }
}
