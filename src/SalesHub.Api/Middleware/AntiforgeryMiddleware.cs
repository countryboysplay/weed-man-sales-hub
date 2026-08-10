using Microsoft.AspNetCore.Antiforgery;

namespace SalesHub.Api.Middleware;

/// <summary>
/// Antiforgery for the JSON API (docs/04): every state-changing /api request
/// must carry the X-CSRF-TOKEN header issued by GET /api/v1/auth/csrf.
/// Login and the forgot-password request are exempt — they are anonymous,
/// credential-gated, and rate limited; a cross-site poster gains nothing.
/// SignalR (/hubs) is excluded: cookies are SameSite=Lax and hub methods are
/// not browser-form targets.
/// </summary>
public sealed class AntiforgeryMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    private static readonly string[] ExemptPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/forgot-password-request",
    ];

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var needsValidation = path.StartsWithSegments("/api")
            && !SafeMethods.Contains(context.Request.Method)
            && !ExemptPaths.Any(exempt => path.Equals(exempt, StringComparison.OrdinalIgnoreCase));

        if (needsValidation)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://saleshub/problems/antiforgery",
                    title = "The request is missing a valid antiforgery token.",
                    status = 400,
                    code = "antiforgery",
                    correlationId = context.TraceIdentifier,
                });
                return;
            }
        }

        await next(context);
    }
}
