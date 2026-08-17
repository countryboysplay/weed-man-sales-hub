using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.OwnerSecurity;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Protected Owner surface (CLAUDE.md §19). Every mutating route here goes
/// through OwnerSecurityService verification — role policy alone is never
/// enough for these.
/// </summary>
public static class OwnerSecurityEndpoints
{
    public static IEndpointRouteBuilder MapOwnerSecurityEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/owner-security").RequireAuthorization(Policies.OwnerOnly);
        group.MapGet("/status", StatusAsync);
        group.MapPost("/master-credential", SetupMasterAsync);
        group.MapPost("/totp/begin", BeginTotpAsync);
        group.MapPost("/totp/confirm", ConfirmTotpAsync);
        group.MapPost("/owner-role", ChangeOwnerRoleAsync);
        group.MapPost("/private-access", StartPrivateAccessAsync);
        group.MapGet("/private-access/{accessSessionId:guid}/conversations/{conversationId:guid}",
            ReadPrivateAsync);
        group.MapPost("/emergency", StartEmergencyAsync);
        group.MapPost("/emergency/{id:guid}/end", EndEmergencyAsync);
        group.MapGet("/events", SecurityEventsAsync);
        return api;
    }

    private static IResult MapCheck(HttpContext http, OwnerSecurityService.ProtectedCheck check) =>
        check.Code switch
        {
            "requiredFreshAuth" => Problems.Forbidden(http, check.Error!, "requiredFreshAuth"),
            "throttled" => Problems.Forbidden(http, check.Error!, "throttled"),
            "invalidCredential" or "invalidTotp" =>
                Problems.Forbidden(http, check.Error!, check.Code),
            "notFound" => Problems.NotFound(http, check.Error!),
            "lastOwner" or "alreadyActive" => Problems.Conflict(http, check.Error!, check.Code!),
            _ => Problems.Validation(http, check.Error!, check.Code ?? "validation"),
        };

    private static async Task<IResult> StatusAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var config = await db.OwnerSecurityConfigs
            .Where(c => c.OwnerUserId == userId)
            .Select(c => new { c.TotpEnabled })
            .FirstOrDefaultAsync(ct);
        return Results.Ok(new
        {
            masterCredentialConfigured = config is not null,
            totpEnabled = config?.TotpEnabled ?? false,
        });
    }

    public sealed record SetupMasterRequest(
        string MasterCredential, string? CurrentMasterCredential, string? TotpCode);

    private static async Task<IResult> SetupMasterAsync(
        SetupMasterRequest request, HttpContext http,
        OwnerSecurityService service, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var check = await service.SetupMasterCredentialAsync(
            userId, session, request.MasterCredential,
            request.CurrentMasterCredential, request.TotpCode, ct);
        return check.Ok ? Results.NoContent() : MapCheck(http, check);
    }

    public sealed record BeginTotpRequest(string MasterCredential);

    private static async Task<IResult> BeginTotpAsync(
        BeginTotpRequest request, HttpContext http,
        OwnerSecurityService service, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var (check, uri) = await service.BeginTotpSetupAsync(
            userId, session, request.MasterCredential,
            http.User.Identity?.Name ?? "owner", ct);
        return check.Ok ? Results.Ok(new { otpauthUri = uri }) : MapCheck(http, check);
    }

    public sealed record ConfirmTotpRequest(string Code);

    private static async Task<IResult> ConfirmTotpAsync(
        ConfirmTotpRequest request, HttpContext http,
        OwnerSecurityService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var check = await service.ConfirmTotpSetupAsync(userId, request.Code, ct);
        return check.Ok ? Results.NoContent() : MapCheck(http, check);
    }

    public sealed record OwnerRoleRequest(
        Guid TargetUserId, string NewRole, string Reason,
        string MasterCredential, string? TotpCode);

    private static async Task<IResult> ChangeOwnerRoleAsync(
        OwnerRoleRequest request, HttpContext http,
        OwnerGovernanceService governance, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var check = await governance.ChangeOwnerRoleAsync(
            userId, session, request.TargetUserId, request.NewRole,
            new OwnerGovernanceService.ProtectedInput(
                request.Reason, request.MasterCredential, request.TotpCode), ct);
        return check.Ok ? Results.NoContent() : MapCheck(http, check);
    }

    public sealed record PrivateAccessRequest(
        List<Guid> ConversationIds, string Scope, string Reason,
        string MasterCredential, string? TotpCode);

    private static async Task<IResult> StartPrivateAccessAsync(
        PrivateAccessRequest request, HttpContext http,
        OwnerGovernanceService governance, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var (check, accessSessionId) = await governance.StartPrivateAccessAsync(
            userId, session, request.ConversationIds, request.Scope,
            new OwnerGovernanceService.ProtectedInput(
                request.Reason, request.MasterCredential, request.TotpCode), ct);
        return check.Ok
            ? Results.Ok(new { accessSessionId })
            : MapCheck(http, check);
    }

    private static async Task<IResult> ReadPrivateAsync(
        Guid accessSessionId, Guid conversationId, HttpContext http,
        OwnerGovernanceService governance, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (error, messages) = await governance.ReadPrivateAsync(
            userId, accessSessionId, conversationId, ct);
        if (error is not null)
        {
            return Problems.Forbidden(http, error, "accessSessionInvalid");
        }

        // Current state only: deleted messages have empty bodies already.
        return Results.Ok(messages!.Select(m => new
        {
            m.Id,
            m.SenderUserId,
            m.Body,
            m.CreatedAtUtc,
            m.EditedAtUtc,
            deleted = m.DeletedAtUtc != null,
        }));
    }

    public sealed record StartEmergencyRequest(
        int DurationMinutes, string Reason, string MasterCredential, string? TotpCode);

    private static async Task<IResult> StartEmergencyAsync(
        StartEmergencyRequest request, HttpContext http,
        OwnerGovernanceService governance, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var (check, emergency) = await governance.StartEmergencyAsync(
            userId, session, request.DurationMinutes,
            new OwnerGovernanceService.ProtectedInput(
                request.Reason, request.MasterCredential, request.TotpCode), ct);
        return check.Ok
            ? Results.Ok(new { emergency!.Id, emergency.ExpiresAtUtc })
            : MapCheck(http, check);
    }

    public sealed record EndEmergencyRequest(string? Reason);

    private static async Task<IResult> EndEmergencyAsync(
        Guid id, EndEmergencyRequest request, HttpContext http,
        OwnerGovernanceService governance, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var error = await governance.EndEmergencyAsync(id, userId, request.Reason, ct);
        return error is null
            ? Results.NoContent()
            : error.Contains("not found")
                ? Problems.NotFound(http, error)
                : Problems.Validation(http, error);
    }

    private static async Task<IResult> SecurityEventsAsync(
        IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.OwnerRecoverySecurityEvents
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(200)
            .Select(e => new { e.Id, e.OwnerUserId, e.EventType, e.Detail, e.OccurredAtUtc })
            .ToListAsync(ct));
}
