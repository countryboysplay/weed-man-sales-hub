using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.OwnerSecurity;

/// <summary>
/// The protected Owner verification core (CLAUDE.md §19, docs/04). Every
/// protected action funnels through <see cref="VerifyProtectedAsync"/>:
/// active Owner session + fresh auth + required reason + master recovery
/// credential (+ TOTP where enabled). Failures throttle and land in the
/// permanent owner_recovery_security_events stream. Nothing here ever logs,
/// returns, or stores credential material.
/// </summary>
public sealed class OwnerSecurityService(
    IAppDb db,
    IMasterCredentialHasher hasher,
    IProtectedSecrets secrets,
    IAuditWriter audit,
    BusinessTime businessTime)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    public sealed record ProtectedCheck(bool Ok, string? Code, string? Error)
    {
        public static readonly ProtectedCheck Success = new(true, null, null);

        public static ProtectedCheck Fail(string code, string error) => new(false, code, error);
    }

    public async Task<bool> IsConfiguredAsync(Guid ownerUserId, CancellationToken ct = default) =>
        await db.OwnerSecurityConfigs.AnyAsync(c => c.OwnerUserId == ownerUserId, ct);

    /// <summary>Initial master credential setup. Requires only fresh auth —
    /// there is nothing to verify against yet. Rotation afterwards demands
    /// the full protected flow.</summary>
    public async Task<ProtectedCheck> SetupMasterCredentialAsync(
        Guid ownerUserId, UserSession session, string masterCredential,
        string? currentMasterCredential, string? totpCode, CancellationToken ct = default)
    {
        if (!HasFreshAuth(session))
        {
            return ProtectedCheck.Fail("requiredFreshAuth", "Fresh authentication required.");
        }

        if (masterCredential is null || masterCredential.Length < 16)
        {
            return ProtectedCheck.Fail(
                "validation", "The master recovery credential needs at least 16 characters.");
        }

        var existing = await db.OwnerSecurityConfigs
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId, ct);
        if (existing is not null)
        {
            // Rotation is itself a protected action.
            var check = await VerifyProtectedAsync(
                ownerUserId, session, "master credential rotation",
                currentMasterCredential ?? "", totpCode, ct);
            if (!check.Ok)
            {
                return check;
            }

            existing.MasterCredentialHash = hasher.Hash(masterCredential);
            existing.UpdatedAtUtc = businessTime.UtcNow;
        }
        else
        {
            db.OwnerSecurityConfigs.Add(new OwnerSecurityConfig
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = ownerUserId,
                MasterCredentialHash = hasher.Hash(masterCredential),
                CreatedAtUtc = businessTime.UtcNow,
            });
        }

        await WriteSecurityEventAsync(ownerUserId,
            existing is null ? "masterCredential.setup" : "masterCredential.rotated", "", ct);
        await audit.WriteAsync(new AuditEntry(
            "ownerSecurity",
            existing is null ? "ownerSecurity.masterCredentialSetup" : "ownerSecurity.masterCredentialRotated",
            AuditRetentionClass.Permanent)
        {
            ActorUserId = ownerUserId,
            SessionId = session.Id,
        }, ct);
        await db.SaveChangesAsync(ct);
        return ProtectedCheck.Success;
    }

    /// <summary>TOTP setup: generates the secret, stores it encrypted, and
    /// returns the otpauth URI exactly once. Enabling requires a valid code,
    /// and — once a master credential exists — the protected flow.</summary>
    public async Task<(ProtectedCheck Check, string? OtpAuthUri)> BeginTotpSetupAsync(
        Guid ownerUserId, UserSession session, string masterCredential,
        string accountLabel, CancellationToken ct = default)
    {
        var config = await db.OwnerSecurityConfigs
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId, ct);
        if (config is null)
        {
            return (ProtectedCheck.Fail("notConfigured", "Set up the master credential first."), null);
        }

        var check = await VerifyProtectedAsync(
            ownerUserId, session, "totp setup", masterCredential, null,
            ct, requireTotp: false);
        if (!check.Ok)
        {
            return (check, null);
        }

        var secret = Totp.NewSecret();
        config.TotpSecretEncrypted = secrets.Protect(secret);
        config.TotpEnabled = false; // armed only after a valid confirmation code
        config.UpdatedAtUtc = businessTime.UtcNow;
        await WriteSecurityEventAsync(ownerUserId, "totp.setupStarted", "", ct);
        await db.SaveChangesAsync(ct);

        var uri = "otpauth://totp/WeedManSalesHub:" + Uri.EscapeDataString(accountLabel)
            + "?secret=" + Totp.ToBase32(secret)
            + "&issuer=WeedManSalesHub&digits=6&period=30";
        return (ProtectedCheck.Success, uri);
    }

    public async Task<ProtectedCheck> ConfirmTotpSetupAsync(
        Guid ownerUserId, string code, CancellationToken ct = default)
    {
        var config = await db.OwnerSecurityConfigs
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId, ct);
        if (config?.TotpSecretEncrypted is null)
        {
            return ProtectedCheck.Fail("notConfigured", "No TOTP setup in progress.");
        }

        var secret = secrets.Unprotect(config.TotpSecretEncrypted);
        if (!Totp.Validate(secret, code, businessTime.UtcNow))
        {
            await WriteSecurityEventAsync(ownerUserId, "totp.confirmFailed", "", ct);
            await db.SaveChangesAsync(ct);
            return ProtectedCheck.Fail("invalidTotp", "The code did not match.");
        }

        config.TotpEnabled = true;
        config.UpdatedAtUtc = businessTime.UtcNow;
        await WriteSecurityEventAsync(ownerUserId, "totp.enabled", "", ct);
        await db.SaveChangesAsync(ct);
        return ProtectedCheck.Success;
    }

    /// <summary>The gate. Session freshness, throttle, master verifier, and
    /// TOTP when enabled (or when explicitly required by the action).</summary>
    public async Task<ProtectedCheck> VerifyProtectedAsync(
        Guid ownerUserId, UserSession session, string reason,
        string masterCredential, string? totpCode,
        CancellationToken ct = default, bool requireTotp = true)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ProtectedCheck.Fail("reasonRequired", "Protected actions require a reason.");
        }

        if (!HasFreshAuth(session))
        {
            return ProtectedCheck.Fail("requiredFreshAuth", "Fresh authentication required.");
        }

        var config = await db.OwnerSecurityConfigs
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId, ct);
        if (config is null)
        {
            return ProtectedCheck.Fail(
                "notConfigured", "The master recovery credential is not set up.");
        }

        var now = businessTime.UtcNow;
        if (config.LockedUntilUtc is { } locked && locked > now)
        {
            return ProtectedCheck.Fail(
                "throttled", "Too many failed attempts. Try again later.");
        }

        if (!hasher.Verify(config.MasterCredentialHash, masterCredential ?? ""))
        {
            await RegisterFailureAsync(config, "masterCredential.verifyFailed", ct);
            return ProtectedCheck.Fail("invalidCredential", "Verification failed.");
        }

        if (config.TotpEnabled && requireTotp)
        {
            var secret = secrets.Unprotect(config.TotpSecretEncrypted!);
            if (!Totp.Validate(secret, totpCode ?? "", now))
            {
                await RegisterFailureAsync(config, "totp.verifyFailed", ct);
                return ProtectedCheck.Fail("invalidTotp", "Verification failed.");
            }
        }

        config.FailedAttempts = 0;
        config.LockedUntilUtc = null;
        await db.SaveChangesAsync(ct);
        return ProtectedCheck.Success;
    }

    private bool HasFreshAuth(UserSession session) =>
        session.FreshAuthUntilUtc is { } until && until > businessTime.UtcNow;

    private async Task RegisterFailureAsync(
        OwnerSecurityConfig config, string eventType, CancellationToken ct)
    {
        config.FailedAttempts += 1;
        if (config.FailedAttempts >= MaxFailures)
        {
            config.LockedUntilUtc = businessTime.UtcNow + LockDuration;
            config.FailedAttempts = 0;
            await WriteSecurityEventAsync(config.OwnerUserId, "protected.lockedOut", "", ct);
        }

        await WriteSecurityEventAsync(config.OwnerUserId, eventType, "", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task WriteSecurityEventAsync(
        Guid? ownerUserId, string eventType, string detail, CancellationToken ct = default)
    {
        db.OwnerRecoverySecurityEvents.Add(new OwnerRecoverySecurityEvent
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerUserId,
            EventType = eventType,
            Detail = detail,
            OccurredAtUtc = businessTime.UtcNow,
        });
        await Task.CompletedTask;
    }
}
