using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.OwnerSecurity;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

/// <summary>
/// Sensitive exports, typed settings with audited changes, and production
/// governance records (PROD/STAGE/ROLL/REC, version lists, maintenance
/// windows). Protected writes verify master credential + TOTP + reason.
/// </summary>
public static class GovernanceEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder api)
    {
        var exports = api.MapGroup("/exports")
            .RequireAuthorization(Policies.ManagerOrOwner);
        exports.MapPost("/employee-history", ExportEmployeeHistoryAsync)
            .RequireAuthorization(Policies.FreshAuthRequired);
        exports.MapGet("/", ListExportsAsync);
        exports.MapGet("/{id:guid}/download", DownloadExportAsync);

        var settings = api.MapGroup("/settings");
        settings.MapGet("/", ListSettingsAsync).RequireAuthorization(Policies.Management);
        settings.MapPut("/management/{key}", PutManagementSettingAsync)
            .RequireAuthorization(Policies.Management);
        settings.MapPut("/system/{key}", PutSystemSettingAsync)
            .RequireAuthorization(Policies.OwnerOnly, Policies.FreshAuthRequired);

        var governance = api.MapGroup("/governance");
        governance.MapGet("/deployments", ListAsync<DeploymentRecord>)
            .RequireAuthorization(Policies.ManagerOrOwner);
        governance.MapPost("/deployments", RecordDeploymentAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapPost("/rollbacks", RecordRollbackAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapPost("/staging-refreshes", RecordStagingRefreshAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapPost("/recoveries", RecordRecoveryAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapPost("/known-good-versions", AddKnownGoodAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapPost("/blocked-rollback-versions", AddBlockedAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapPost("/maintenance-windows", AddMaintenanceAsync)
            .RequireAuthorization(Policies.OwnerOnly);
        governance.MapGet("/maintenance-windows", ListMaintenanceAsync)
            .RequireAuthorization(Policies.Management);

        return api;
    }

    // ── sensitive exports ─────────────────────────────────────────────────────

    public sealed record ExportEmployeeHistoryRequest(
        Guid TargetUserId, string Format, string Reason);

    private static async Task<IResult> ExportEmployeeHistoryAsync(
        ExportEmployeeHistoryRequest request, HttpContext http,
        SensitiveExportService exportsService, IIdentityService identity, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var actor = await identity.FindByIdAsync(userId, ct);
        var result = await exportsService.ExportEmployeeHistoryAsync(
            userId, actor?.DisplayName ?? "Unknown", request.TargetUserId,
            request.Format, request.Reason, ct);
        return result.Export is not null
            ? Results.Created($"/api/v1/exports/{result.Export.Id}",
                new { result.Export.Id, result.Export.PublicId })
            : Problems.Validation(http, result.Error!);
    }

    private static async Task<IResult> ListExportsAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.SensitiveExports
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(200)
            .Select(e => new
            {
                e.Id,
                e.PublicId,
                e.Kind,
                e.TargetUserId,
                e.Format,
                e.RequestedByUserId,
                e.CreatedAtUtc,
                Downloads = db.SensitiveExportAccesses.Count(a => a.ExportId == e.Id),
            })
            .ToListAsync(ct));

    private static async Task<IResult> DownloadExportAsync(
        Guid id, HttpContext http, SensitiveExportService exportsService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (content, export) = await exportsService.DownloadAsync(id, userId, ct);
        return content is null || export is null
            ? Problems.NotFound(http, "Export not found.")
            : Results.Stream(content,
                export.Format == "Pdf" ? "application/pdf" : "text/csv",
                $"{export.PublicId}.{export.Format.ToLowerInvariant()}");
    }

    // ── settings ──────────────────────────────────────────────────────────────

    private static async Task<IResult> ListSettingsAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var role = http.Items[AuthConstants.UserRoleItemKey] as string ?? "";
        var query = db.Settings.AsQueryable();
        if (role != Roles.Owner)
        {
            query = query.Where(s => s.Scope == SettingScope.Management);
        }

        return Results.Ok(await query
            .OrderBy(s => s.Key)
            .Select(s => new
            {
                s.Key,
                s.ValueJson,
                Scope = s.Scope.ToString(),
                s.UpdatedAtUtc,
            })
            .ToListAsync(ct));
    }

    public sealed record PutSettingRequest(JsonElement Value);

    private static Task<IResult> PutManagementSettingAsync(
        string key, PutSettingRequest request, HttpContext http, IAppDb db,
        IAuditWriter audit, BusinessTime businessTime, CancellationToken ct) =>
        PutSettingAsync(key, request, SettingScope.Management, http, db, audit, businessTime, ct);

    private static Task<IResult> PutSystemSettingAsync(
        string key, PutSettingRequest request, HttpContext http, IAppDb db,
        IAuditWriter audit, BusinessTime businessTime, CancellationToken ct) =>
        PutSettingAsync(key, request, SettingScope.System, http, db, audit, businessTime, ct);

    private static async Task<IResult> PutSettingAsync(
        string key, PutSettingRequest request, SettingScope scope, HttpContext http,
        IAppDb db, IAuditWriter audit, BusinessTime businessTime, CancellationToken ct)
    {
        key = key.Trim();
        if (key.Length is 0 or > 128)
        {
            return Problems.Validation(http, "A setting key runs 1-128 characters.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var valueJson = request.Value.GetRawText();
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var before = setting?.ValueJson;
        if (setting is null)
        {
            setting = new SettingEntry
            {
                Id = Guid.CreateVersion7(),
                Key = key,
                Scope = scope,
            };
            db.Settings.Add(setting);
        }
        else if (setting.Scope != scope)
        {
            // A system setting cannot be rewritten through the management route.
            return Problems.Forbidden(http, "That setting belongs to a different scope.");
        }

        setting.ValueJson = valueJson;
        setting.UpdatedByUserId = userId;
        setting.UpdatedAtUtc = businessTime.UtcNow;

        // Settings audit (docs/10 Wave 6): who, what, before/after.
        await audit.WriteAsync(new AuditEntry(
            "settings", "settings.changed",
            scope == SettingScope.System
                ? AuditRetentionClass.Permanent
                : AuditRetentionClass.Operational365Days)
        {
            ActorUserId = userId,
            TargetType = "Setting",
            TargetId = key,
            Before = before is null ? null : new { valueJson = before },
            After = new { valueJson },
        }, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── governance records ────────────────────────────────────────────────────

    public sealed record DeploymentRequest(string Version, bool Success, string? Notes);

    private static async Task<IResult> RecordDeploymentAsync(
        DeploymentRequest request, HttpContext http, IAppDb db,
        IPublicIdGenerator publicIds, BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Version))
        {
            return Problems.Validation(http, "A deployment record needs a version.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var record = new DeploymentRecord
        {
            Id = Guid.CreateVersion7(),
            PublicId = await publicIds.NextAsync("PROD", ct),
            Version = request.Version.Trim(),
            Success = request.Success,
            Notes = request.Notes?.Trim() ?? "",
            RecordedByUserId = userId,
            DeployedAtUtc = businessTime.UtcNow,
        };
        db.DeploymentRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/governance/deployments/{record.Id}",
            new { record.Id, record.PublicId });
    }

    public sealed record RollbackRequest(
        string FromVersion, string ToVersion, string Reason,
        string MasterCredential, string? TotpCode);

    private static async Task<IResult> RecordRollbackAsync(
        RollbackRequest request, HttpContext http, IAppDb db,
        OwnerSecurityService ownerSecurity, IPublicIdGenerator publicIds,
        IAuditWriter audit, BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var check = await ownerSecurity.VerifyProtectedAsync(
            userId, session, request.Reason, request.MasterCredential, request.TotpCode, ct);
        if (!check.Ok)
        {
            return Problems.Forbidden(http, check.Error!, check.Code ?? "protected");
        }

        var blocked = await db.BlockedRollbackVersions
            .AnyAsync(v => v.Version == request.ToVersion, ct);
        if (blocked)
        {
            return Problems.Conflict(http,
                "That version is blocked for rollback.", "rollbackBlocked");
        }

        var record = new RollbackRecord
        {
            Id = Guid.CreateVersion7(),
            PublicId = await publicIds.NextAsync("ROLL", ct),
            FromVersion = request.FromVersion?.Trim() ?? "",
            ToVersion = request.ToVersion.Trim(),
            Reason = request.Reason.Trim(),
            RecordedByUserId = userId,
            OccurredAtUtc = businessTime.UtcNow,
        };
        db.RollbackRecords.Add(record);
        await audit.WriteAsync(new AuditEntry(
            "governance", "governance.rollback", AuditRetentionClass.Permanent)
        {
            ActorUserId = userId,
            TargetType = "RollbackRecord",
            TargetId = record.Id.ToString(),
            PublicRecordId = record.PublicId,
            Reason = record.Reason,
        }, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/governance/rollbacks/{record.Id}",
            new { record.Id, record.PublicId });
    }

    public sealed record StagingRefreshRequest(
        string Reason, string MasterCredential, string? TotpCode);

    private static async Task<IResult> RecordStagingRefreshAsync(
        StagingRefreshRequest request, HttpContext http, IAppDb db,
        OwnerSecurityService ownerSecurity, IPublicIdGenerator publicIds,
        IAuditWriter audit, BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var check = await ownerSecurity.VerifyProtectedAsync(
            userId, session, request.Reason, request.MasterCredential, request.TotpCode, ct);
        if (!check.Ok)
        {
            return Problems.Forbidden(http, check.Error!, check.Code ?? "protected");
        }

        var record = new StagingRecord
        {
            Id = Guid.CreateVersion7(),
            PublicId = await publicIds.NextAsync("STAGE", ct),
            Reason = request.Reason.Trim(),
            RequestedByUserId = userId,
            RefreshedAtUtc = businessTime.UtcNow,
        };
        db.StagingRecords.Add(record);
        await audit.WriteAsync(new AuditEntry(
            "governance", "governance.stagingRefresh", AuditRetentionClass.Permanent)
        {
            ActorUserId = userId,
            TargetType = "StagingRecord",
            TargetId = record.Id.ToString(),
            PublicRecordId = record.PublicId,
            Reason = record.Reason,
        }, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/governance/staging-refreshes/{record.Id}",
            new { record.Id, record.PublicId });
    }

    public sealed record RecoveryRequest(
        Guid ArchiveEntryId, string SourceDescription, string Reason,
        string MasterCredential, string? TotpCode);

    private static async Task<IResult> RecordRecoveryAsync(
        RecoveryRequest request, HttpContext http, IAppDb db,
        OwnerSecurityService ownerSecurity, IPublicIdGenerator publicIds,
        IAuditWriter audit, BusinessTime businessTime, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        var check = await ownerSecurity.VerifyProtectedAsync(
            userId, session, request.Reason, request.MasterCredential, request.TotpCode, ct);
        if (!check.Ok)
        {
            return Problems.Forbidden(http, check.Error!, check.Code ?? "protected");
        }

        var entry = await db.ArchiveEntries
            .FirstOrDefaultAsync(a => a.Id == request.ArchiveEntryId, ct);
        if (entry is null)
        {
            return Problems.NotFound(http, "Archive entry not found.");
        }

        var record = new RecoveryRecord
        {
            Id = Guid.CreateVersion7(),
            PublicId = await publicIds.NextAsync("REC", ct),
            OwnerUserId = userId,
            ArchiveEntryId = entry.Id,
            SourceDescription = request.SourceDescription?.Trim() ?? "",
            Reason = request.Reason.Trim(),
            CreatedAtUtc = businessTime.UtcNow,
        };
        db.RecoveryRecords.Add(record);

        // The recovered artifact is marked, the original stays (CLAUDE.md §20).
        entry.Recovered = true;
        entry.RecoveredFromNote =
            $"{record.PublicId} — {record.SourceDescription} at {record.CreatedAtUtc:u}";

        await audit.WriteAsync(new AuditEntry(
            "governance", "governance.reportRecovery", AuditRetentionClass.Permanent)
        {
            ActorUserId = userId,
            TargetType = "RecoveryRecord",
            TargetId = record.Id.ToString(),
            PublicRecordId = record.PublicId,
            Reason = record.Reason,
        }, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/governance/recoveries/{record.Id}",
            new { record.Id, record.PublicId });
    }

    public sealed record VersionRequest(string Version, string? Reason);

    private static async Task<IResult> AddKnownGoodAsync(
        VersionRequest request, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Version))
        {
            return Problems.Validation(http, "A version is required.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        if (!await db.KnownGoodVersions.AnyAsync(v => v.Version == request.Version, ct))
        {
            db.KnownGoodVersions.Add(new KnownGoodVersion
            {
                Id = Guid.CreateVersion7(),
                Version = request.Version.Trim(),
                RecordedByUserId = userId,
                RecordedAtUtc = businessTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> AddBlockedAsync(
        VersionRequest request, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Version) || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Problems.Validation(http, "Blocking a version needs the version and a reason.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        if (!await db.BlockedRollbackVersions.AnyAsync(v => v.Version == request.Version, ct))
        {
            db.BlockedRollbackVersions.Add(new BlockedRollbackVersion
            {
                Id = Guid.CreateVersion7(),
                Version = request.Version.Trim(),
                Reason = request.Reason.Trim(),
                RecordedByUserId = userId,
                RecordedAtUtc = businessTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    public sealed record MaintenanceRequest(
        DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string Reason);

    private static async Task<IResult> AddMaintenanceAsync(
        MaintenanceRequest request, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (request.EndAtUtc <= request.StartAtUtc || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Problems.Validation(http, "A maintenance window needs a valid range and reason.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var window = new MaintenanceWindow
        {
            Id = Guid.CreateVersion7(),
            StartAtUtc = request.StartAtUtc,
            EndAtUtc = request.EndAtUtc,
            Reason = request.Reason.Trim(),
            CreatedByUserId = userId,
            CreatedAtUtc = businessTime.UtcNow,
        };
        db.MaintenanceWindows.Add(window);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/governance/maintenance-windows/{window.Id}",
            new { window.Id });
    }

    private static async Task<IResult> ListMaintenanceAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.MaintenanceWindows
            .Where(w => w.CanceledAtUtc == null)
            .OrderByDescending(w => w.StartAtUtc)
            .Take(50)
            .ToListAsync(ct));

    private static async Task<IResult> ListAsync<T>(IAppDb db, CancellationToken ct)
        where T : class
    {
        // Only DeploymentRecord uses this today; keep it simple and typed.
        if (typeof(T) == typeof(DeploymentRecord))
        {
            return Results.Ok(await db.DeploymentRecords
                .OrderByDescending(r => r.DeployedAtUtc)
                .Take(100)
                .ToListAsync(ct));
        }

        return Results.NotFound();
    }
}
