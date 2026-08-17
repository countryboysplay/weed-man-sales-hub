using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.OwnerSecurity;

/// <summary>
/// The protected Owner actions themselves (CLAUDE.md §19): Owner
/// promotion/demotion/creation, private-communication inspection, and
/// emergency access. Every entry point verifies through
/// <see cref="OwnerSecurityService"/> first and writes permanent audit.
/// </summary>
public sealed class OwnerGovernanceService(
    IAppDb db,
    IIdentityService identity,
    OwnerSecurityService ownerSecurity,
    IAuditWriter audit,
    NotificationService notifications,
    BusinessTime businessTime)
{
    public sealed record ProtectedInput(string Reason, string MasterCredential, string? TotpCode);

    private static readonly TimeSpan PrivateAccessWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxEmergencyDuration = TimeSpan.FromMinutes(60);

    // ── Owner lifecycle ───────────────────────────────────────────────────────

    public async Task<OwnerSecurityService.ProtectedCheck> ChangeOwnerRoleAsync(
        Guid actorOwnerId, UserSession session, Guid targetUserId, string newRole,
        ProtectedInput input, CancellationToken ct = default)
    {
        var check = await ownerSecurity.VerifyProtectedAsync(
            actorOwnerId, session, input.Reason, input.MasterCredential, input.TotpCode, ct);
        if (!check.Ok)
        {
            return check;
        }

        if (!Roles.IsValid(newRole))
        {
            return OwnerSecurityService.ProtectedCheck.Fail("validation", $"Unknown role '{newRole}'.");
        }

        var target = await identity.FindByIdAsync(targetUserId, ct);
        if (target is null)
        {
            return OwnerSecurityService.ProtectedCheck.Fail("notFound", "User not found.");
        }

        if (target.Role != Roles.Owner && newRole != Roles.Owner)
        {
            return OwnerSecurityService.ProtectedCheck.Fail(
                "validation", "Neither side of this change involves Owner; use the ordinary flow.");
        }

        // Demoting the last Owner would lock the org out of every protected flow.
        if (target.Role == Roles.Owner && newRole != Roles.Owner)
        {
            var owners = await identity.ListUsersAsync(new UserQuery(Role: Roles.Owner), ct);
            if (owners.Count(o => o.IsActive) <= 1)
            {
                return OwnerSecurityService.ProtectedCheck.Fail(
                    "lastOwner", "The last active Owner cannot be demoted.");
            }
        }

        await identity.SetRoleProtectedAsync(targetUserId, newRole, ct);
        await audit.WriteAsync(new AuditEntry(
            "ownerSecurity", "ownerSecurity.ownerRoleChanged", AuditRetentionClass.Permanent)
        {
            ActorUserId = actorOwnerId,
            TargetType = "User",
            TargetId = targetUserId.ToString(),
            Reason = input.Reason,
            Before = new { role = target.Role },
            After = new { role = newRole },
            SessionId = session.Id,
        }, ct);
        await ownerSecurity.WriteSecurityEventAsync(
            actorOwnerId, "owner.roleChanged", $"target={targetUserId}", ct);
        await db.SaveChangesAsync(ct);
        return OwnerSecurityService.ProtectedCheck.Success;
    }

    // ── private communication inspection ──────────────────────────────────────

    public async Task<(OwnerSecurityService.ProtectedCheck Check, Guid? AccessSessionId)>
        StartPrivateAccessAsync(
            Guid ownerId, UserSession session, IReadOnlyList<Guid> conversationIds,
            string scope, ProtectedInput input, CancellationToken ct = default)
    {
        var check = await ownerSecurity.VerifyProtectedAsync(
            ownerId, session, input.Reason, input.MasterCredential, input.TotpCode, ct);
        if (!check.Ok)
        {
            return (check, null);
        }

        if (conversationIds.Count == 0)
        {
            return (OwnerSecurityService.ProtectedCheck.Fail(
                "validation", "Choose at least one conversation."), null);
        }

        var existing = await db.Conversations
            .Where(c => conversationIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (existing.Count != conversationIds.Distinct().Count())
        {
            return (OwnerSecurityService.ProtectedCheck.Fail(
                "notFound", "One or more conversations do not exist."), null);
        }

        // The permanent access record exists BEFORE any content is returned
        // (docs/04 step 5).
        var now = businessTime.UtcNow;
        var access = new PrivateCommunicationAccess
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerId,
            Scope = scope,
            TargetConversationIdsJson = JsonSerializer.Serialize(existing),
            Reason = input.Reason,
            AccessSessionId = Guid.CreateVersion7(),
            StartedAtUtc = now,
            ExpiresAtUtc = now + PrivateAccessWindow,
        };
        db.PrivateCommunicationAccesses.Add(access);
        await audit.WriteAsync(new AuditEntry(
            "ownerSecurity", "ownerSecurity.privateCommunicationAccess",
            AuditRetentionClass.Permanent)
        {
            ActorUserId = ownerId,
            TargetType = "Conversations",
            TargetId = string.Join(",", existing),
            Reason = input.Reason,
            SessionId = session.Id,
        }, ct);
        await db.SaveChangesAsync(ct);
        return (OwnerSecurityService.ProtectedCheck.Success, access.AccessSessionId);
    }

    /// <summary>Reads current state only under an unexpired access session.
    /// Deleted messages stay deleted — their bodies were erased at delete
    /// time and are not reconstructed here (CLAUDE.md §7).</summary>
    public async Task<(string? Error, IReadOnlyList<Message>? Messages)> ReadPrivateAsync(
        Guid ownerId, Guid accessSessionId, Guid conversationId, CancellationToken ct = default)
    {
        var access = await db.PrivateCommunicationAccesses.FirstOrDefaultAsync(
            a => a.AccessSessionId == accessSessionId && a.OwnerUserId == ownerId, ct);
        if (access is null || access.EndedAtUtc is not null
            || access.ExpiresAtUtc < businessTime.UtcNow)
        {
            return ("The access session has expired.", null);
        }

        var inScope = JsonSerializer
            .Deserialize<List<Guid>>(access.TargetConversationIdsJson)!
            .Contains(conversationId);
        if (!inScope)
        {
            return ("That conversation is outside the approved scope.", null);
        }

        // Child access metadata (docs/04 step 7) — never content.
        await audit.WriteAsync(new AuditEntry(
            "ownerSecurity", "ownerSecurity.privateCommunicationRead",
            AuditRetentionClass.Permanent)
        {
            ActorUserId = ownerId,
            TargetType = "Conversation",
            TargetId = conversationId.ToString(),
        }, ct);
        await db.SaveChangesAsync(ct);

        var messages = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(500)
            .ToListAsync(ct);
        return (null, messages);
    }

    // ── emergency access ──────────────────────────────────────────────────────

    public async Task<(OwnerSecurityService.ProtectedCheck Check, EmergencyAccessSession? Session)>
        StartEmergencyAsync(
            Guid ownerId, UserSession session, int durationMinutes,
            ProtectedInput input, CancellationToken ct = default)
    {
        var check = await ownerSecurity.VerifyProtectedAsync(
            ownerId, session, input.Reason, input.MasterCredential, input.TotpCode, ct);
        if (!check.Ok)
        {
            return (check, null);
        }

        var duration = TimeSpan.FromMinutes(durationMinutes);
        if (duration <= TimeSpan.Zero || duration > MaxEmergencyDuration)
        {
            return (OwnerSecurityService.ProtectedCheck.Fail(
                "validation", "Emergency access runs 1-60 minutes."), null);
        }

        var active = await db.EmergencyAccessSessions.AnyAsync(
            s => s.OwnerUserId == ownerId && s.EndedAtUtc == null
                && s.ExpiresAtUtc > businessTime.UtcNow, ct);
        if (active)
        {
            return (OwnerSecurityService.ProtectedCheck.Fail(
                "alreadyActive", "An emergency session is already active."), null);
        }

        var now = businessTime.UtcNow;
        var emergency = new EmergencyAccessSession
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerId,
            Reason = input.Reason,
            StartedAtUtc = now,
            ExpiresAtUtc = now + duration,
        };
        db.EmergencyAccessSessions.Add(emergency);
        await audit.WriteAsync(new AuditEntry(
            "ownerSecurity", "ownerSecurity.emergencyStarted", AuditRetentionClass.Permanent)
        {
            ActorUserId = ownerId,
            TargetType = "EmergencyAccessSession",
            TargetId = emergency.Id.ToString(),
            Reason = input.Reason,
            SessionId = session.Id,
        }, ct);
        await NotifyOtherOwnersAsync(ownerId,
            "Emergency access started",
            $"An Owner opened emergency access until {businessTime.ToLocal(emergency.ExpiresAtUtc):h:mm tt}.",
            ct);
        await db.SaveChangesAsync(ct);
        return (OwnerSecurityService.ProtectedCheck.Success, emergency);
    }

    /// <summary>End your own session, or terminate another Owner's — the
    /// latter requires a reason and notifies (CLAUDE.md §19).</summary>
    public async Task<string?> EndEmergencyAsync(
        Guid emergencySessionId, Guid actorOwnerId, string? reason, CancellationToken ct = default)
    {
        var emergency = await db.EmergencyAccessSessions
            .FirstOrDefaultAsync(s => s.Id == emergencySessionId && s.EndedAtUtc == null, ct);
        if (emergency is null)
        {
            return "Active emergency session not found.";
        }

        var terminatingAnother = emergency.OwnerUserId != actorOwnerId;
        if (terminatingAnother && string.IsNullOrWhiteSpace(reason))
        {
            return "Terminating another Owner's session requires a reason.";
        }

        emergency.EndedAtUtc = businessTime.UtcNow;
        emergency.EndedByUserId = actorOwnerId;
        emergency.EndReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await audit.WriteAsync(new AuditEntry(
            "ownerSecurity",
            terminatingAnother ? "ownerSecurity.emergencyTerminated" : "ownerSecurity.emergencyEnded",
            AuditRetentionClass.Permanent)
        {
            ActorUserId = actorOwnerId,
            TargetType = "EmergencyAccessSession",
            TargetId = emergency.Id.ToString(),
            Reason = emergency.EndReason,
        }, ct);
        await NotifyOtherOwnersAsync(actorOwnerId,
            "Emergency access ended",
            terminatingAnother
                ? "An Owner terminated another Owner's emergency session."
                : "An Owner's emergency session ended.",
            ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private async Task NotifyOtherOwnersAsync(
        Guid actorOwnerId, string title, string preview, CancellationToken ct)
    {
        var owners = await identity.ListUsersAsync(new UserQuery(Role: Roles.Owner), ct);
        foreach (var owner in owners.Where(o => o.Id != actorOwnerId))
        {
            _ = await notifications.CreateAsync(owner.Id,
                new NotificationService.NewNotification(
                    "security", title, preview, Required: true), ct);
        }
    }
}
