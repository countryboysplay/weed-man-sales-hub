using Serilog.Context;
using SalesHub.Infrastructure.Services;

namespace SalesHub.Api.Middleware;

/// <summary>
/// Accepts or mints the request correlation id, exposes it on the response,
/// pushes it into the log scope and the ambient CorrelationContext so audit
/// rows and ProblemDetails all carry the same id (docs/04).
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64
            ? incoming
            : CorrelationContext.NewId();

        CorrelationContext.Set(correlationId);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
