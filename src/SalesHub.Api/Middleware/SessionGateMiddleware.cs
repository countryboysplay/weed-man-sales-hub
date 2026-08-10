using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Auth;
using SalesHub.Domain;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Middleware;

/// <summary>
/// The server-side session gate (CLAUDE.md §3): every authenticated request
/// must map to an active user_sessions row whose verifier matches the cookie.
/// A revoked or missing session is signed out and refused immediately —
/// cookie lifetime is irrelevant. The validated session rides in
/// HttpContext.Items for policies (fresh auth, idle capability) downstream.
/// </summary>
public sealed class SessionGateMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromSeconds(60);

    public async Task InvokeAsync(HttpContext context, SalesHubDbContext db, BusinessTime businessTime)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var sessionIdRaw = context.User.FindFirstValue(AuthConstants.SessionIdClaim);
        var verifier = context.User.FindFirstValue(AuthConstants.SessionVerifierClaim);

        if (!Guid.TryParse(sessionIdRaw, out var sessionId) || string.IsNullOrEmpty(verifier))
        {
            await RefuseAsync(context);
            return;
        }

        var session = await db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, context.RequestAborted);

        if (session is null
            || session.RevokedAtUtc is not null
            || !SessionTokens.Matches(verifier, session.TokenHash))
        {
            await RefuseAsync(context);
            return;
        }

        var now = businessTime.UtcNow;
        if (now - session.LastSeenAtUtc > LastSeenWriteInterval)
        {
            session.LastSeenAtUtc = now;
            await db.SaveChangesAsync(context.RequestAborted);
        }

        context.Items[AuthConstants.SessionItemKey] = session;
        context.Items[AuthConstants.UserRoleItemKey] =
            context.User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

        await next(context);
    }

    private static async Task RefuseAsync(HttpContext context)
    {
        await context.SignOutAsync(AuthConstants.CookieScheme);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://saleshub/problems/session-revoked",
            title = "Your session has ended.",
            status = 401,
            code = "sessionRevoked",
            correlationId = context.TraceIdentifier,
        });
    }
}
