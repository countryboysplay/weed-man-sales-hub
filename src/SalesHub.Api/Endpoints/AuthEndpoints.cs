using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Auth;
using AuthenticationService = SalesHub.Application.Auth.AuthenticationService;
using SalesHub.Contracts.Auth;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth");

        auth.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(Policies.Employee);

        auth.MapGet("/me", MeAsync)
            .RequireAuthorization(Policies.Employee);

        auth.MapGet("/sessions", ListSessionsAsync)
            .RequireAuthorization(Policies.Employee);

        auth.MapDelete("/sessions/{sessionId:guid}", RevokeSessionAsync)
            .RequireAuthorization(Policies.Employee);

        auth.MapPost("/fresh-auth", FreshAuthAsync)
            .RequireAuthorization(Policies.Employee)
            .RequireRateLimiting("auth");

        auth.MapPost("/forgot-password-request", ForgotPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting("auth");

        auth.MapPost("/idle-capability/verify", VerifyIdleCapabilityAsync)
            .RequireAuthorization(Policies.Employee);

        auth.MapPost("/idle-capability/heartbeat", IdleHeartbeatAsync)
            .RequireAuthorization(Policies.Employee);

        return api;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        AuthenticationService authService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Problems.Validation(http, "Username and password are required.");
        }

        var outcome = await authService.LoginAsync(new AuthenticationService.LoginInput(
            request.Username.Trim(),
            request.Password,
            request.Device?.DeviceId ?? string.Empty,
            request.Device?.BrowserFamily ?? string.Empty,
            request.Device?.OsFamily ?? string.Empty,
            request.Device?.PwaInstalled ?? false,
            request.Device?.AppVersion ?? string.Empty,
            HashIp(http.Connection.RemoteIpAddress?.ToString())), ct);

        switch (outcome.Result)
        {
            case CredentialCheckOutcome.LockedOut:
                return Problems.Auth(http, "accountLocked",
                    "Too many failed attempts. Try again shortly.");
            case CredentialCheckOutcome.Deactivated:
                // Mirrors the approved "Account deactivated" access state.
                return Problems.Auth(http, "accountDeactivated",
                    "This account is not currently active. Contact management.");
            case CredentialCheckOutcome.InvalidCredentials:
                return Problems.Auth(http, "invalidCredentials",
                    "The username or password is incorrect.");
        }

        var user = outcome.User!;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(AuthConstants.SessionIdClaim, outcome.SessionId.ToString()),
            new(AuthConstants.SessionVerifierClaim, outcome.Verifier),
        };
        var identity = new ClaimsIdentity(claims, AuthConstants.CookieScheme);
        await http.SignInAsync(
            AuthConstants.CookieScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                // "Keep me signed in": persistent cookie; the server-side
                // session record is the real authority either way.
                IsPersistent = request.KeepSignedIn,
            });

        return Results.Ok(new LoginResponse(
            user.Id, user.Username, user.DisplayName, user.Role,
            outcome.SessionId, outcome.IdleCapabilityRequired));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http, AuthenticationService authService, CancellationToken ct)
    {
        var (userId, session) = Current(http);
        await authService.LogoutAsync(session.Id, userId, ct);
        await http.SignOutAsync(AuthConstants.CookieScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        HttpContext http,
        AuthenticationService authService,
        IIdentityService identity,
        CancellationToken ct)
    {
        var (userId, session) = Current(http);
        var user = await identity.FindByIdAsync(userId, ct);
        if (user is null)
        {
            return Problems.NotFound(http, "User not found.");
        }

        return Results.Ok(new MeResponse(
            userId,
            user.Username,
            user.DisplayName,
            user.Role,
            session.Id,
            session.IdleCapabilityState.ToString(),
            authService.IsMonitoredRole(user.Role),
            session.FreshAuthUntilUtc));
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var (userId, current) = Current(http);
        var sessions = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .Select(s => new SessionDto(
                s.Id, s.CreatedAtUtc, s.LastSeenAtUtc, s.BrowserFamily,
                s.OsFamily, s.PwaInstalled, s.AppVersion, s.Id == current.Id))
            .ToListAsync(ct);
        return Results.Ok(sessions);
    }

    private static async Task<IResult> RevokeSessionAsync(
        Guid sessionId, HttpContext http, AuthenticationService authService, CancellationToken ct)
    {
        var (userId, _) = Current(http);
        var role = http.User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var result = await authService.RevokeSessionAsync(
            sessionId, userId, role,
            SessionRevocationReason.UserSignedOutOtherDevice, ct);

        return result switch
        {
            AuthenticationService.RevocationResult.Revoked => Results.NoContent(),
            AuthenticationService.RevocationResult.NotFound => Problems.NotFound(http, "Session not found."),
            _ => Problems.Forbidden(http, "You may not revoke that session."),
        };
    }

    private static async Task<IResult> FreshAuthAsync(
        FreshAuthRequest request, HttpContext http, AuthenticationService authService, CancellationToken ct)
    {
        var (_, session) = Current(http);
        var username = http.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var until = await authService.FreshAuthAsync(session.Id, username, request.Password, ct);
        return until is null
            ? Problems.Auth(http, "invalidCredentials", "The password is incorrect.")
            : Results.Ok(new FreshAuthResponse(until.Value));
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        HttpContext http,
        SalesHub.Application.Users.PasswordResetService passwordResets,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Problems.Validation(http, "A username is required.");
        }

        // Management-mediated recovery (CLAUDE.md §3): the request lands in
        // the management queue and management is notified. No email, no
        // token, and — deliberately — the same 202 whether or not the
        // username exists.
        await passwordResets.SubmitAsync(request.Username, ct);
        return Results.Accepted(value: new
        {
            message = "If the account exists, management has been notified.",
        });
    }

    private static async Task<IResult> VerifyIdleCapabilityAsync(
        IdleCapabilityVerifyRequest request,
        HttpContext http,
        IdleCapabilityService idleCapability,
        CancellationToken ct)
    {
        var (_, session) = Current(http);
        var status = await idleCapability.VerifyAsync(session.Id, new IdleCapabilityService.VerifyInput(
            request.Supported, request.Permission, request.DetectorStarted, request.ThresholdSeconds), ct);
        return status is null
            ? Problems.NotFound(http, "Session not found.")
            : Results.Ok(new IdleCapabilityVerifyResponse(
                status.State.ToString(), status.LeaseUntil, status.HeartbeatCadenceSeconds));
    }

    private static async Task<IResult> IdleHeartbeatAsync(
        IdleHeartbeatRequest request,
        HttpContext http,
        IdleCapabilityService idleCapability,
        CancellationToken ct)
    {
        var (_, session) = Current(http);
        var status = await idleCapability.HeartbeatAsync(
            session.Id, request.UserState, request.ScreenState, ct);
        return status is null
            ? Problems.NotFound(http, "Session not found.")
            : Results.Ok(new IdleHeartbeatResponse(
                status.State.ToString(), status.LeaseUntil, status.HeartbeatCadenceSeconds));
    }

    internal static (Guid UserId, UserSession Session) Current(HttpContext http)
    {
        var userId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var session = (UserSession)http.Items[AuthConstants.SessionItemKey]!;
        return (userId, session);
    }

    private static string HashIp(string? ip) =>
        string.IsNullOrEmpty(ip)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip)))[..32];
}
