using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Auth;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Users;

/// <summary>
/// The management-mediated forgot-password flow (CLAUDE.md §3, login-states
/// mockup): a request lands in the management queue and every management
/// user — including Owners, per the 2026-08-10 decision — is notified.
/// No email token, no self-service link, and the anonymous endpoint answers
/// identically whether or not the username exists.
/// </summary>
public sealed class PasswordResetService(
    IAppDb db,
    IIdentityService identity,
    UserLifecycleService lifecycle,
    NotificationService notifications,
    IAuditWriter audit,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    public async Task SubmitAsync(string usernameSubmitted, CancellationToken ct = default)
    {
        var username = usernameSubmitted.Trim();
        var matched = await identity.FindByUsernameAsync(username, ct);

        var request = new PasswordResetRequest
        {
            Id = Guid.CreateVersion7(),
            UsernameSubmitted = username,
            MatchedUserId = matched?.Id,
            CreatedAtUtc = businessTime.UtcNow,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.PasswordResetRequests.Add(request);
            await audit.WriteAsync(new AuditEntry(
                "auth", "auth.forgotPasswordRequested", AuditRetentionClass.Operational365Days)
            {
                TargetType = "PasswordResetRequest",
                TargetId = request.Id.ToString(),
            }, token);
            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                Category: "security",
                Title: "Password reset requested",
                SafePreview: $"A password reset was requested for '{username}'.",
                Required: false,
                ReferenceType: "PasswordResetRequest",
                ReferenceId: request.Id.ToString()), excludeUserId: null, token);
            await outbox.EnqueueAsync(EventTypes.PasswordResetRequested, new
            {
                requestId = request.Id,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    public async Task<List<PasswordResetRequest>> ListOpenAsync(CancellationToken ct = default) =>
        await db.PasswordResetRequests
            .Where(r => r.Status == PasswordResetRequestStatus.Open)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);

    public enum CompleteOutcome { Done, NotFound, NoMatchedUser, Forbidden, Invalid }

    /// <summary>Management assigns the replacement password. Runs through the
    /// lifecycle reset so session revocation and Owner protection apply.</summary>
    public async Task<(CompleteOutcome Outcome, string? Error)> CompleteAsync(
        Guid requestId, UserLifecycleService.Actor actor, string newPassword,
        CancellationToken ct = default)
    {
        var request = await db.PasswordResetRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.Status == PasswordResetRequestStatus.Open, ct);
        if (request is null)
        {
            return (CompleteOutcome.NotFound, null);
        }

        if (request.MatchedUserId is not { } targetUserId)
        {
            return (CompleteOutcome.NoMatchedUser,
                "This request does not match any account; dismiss it.");
        }

        var reset = await lifecycle.ResetPasswordAsync(targetUserId, actor, newPassword, ct);
        switch (reset.Outcome)
        {
            case UserLifecycleService.Outcome.Forbidden:
                return (CompleteOutcome.Forbidden, reset.Error);
            case UserLifecycleService.Outcome.Invalid:
                return (CompleteOutcome.Invalid, reset.Error);
            case UserLifecycleService.Outcome.NotFound:
                return (CompleteOutcome.NoMatchedUser, "The matched account no longer exists.");
        }

        request.Status = PasswordResetRequestStatus.Completed;
        request.HandledByUserId = actor.UserId;
        request.HandledAtUtc = businessTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (CompleteOutcome.Done, null);
    }

    public async Task<bool> DismissAsync(
        Guid requestId, UserLifecycleService.Actor actor, CancellationToken ct = default)
    {
        var request = await db.PasswordResetRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.Status == PasswordResetRequestStatus.Open, ct);
        if (request is null)
        {
            return false;
        }

        request.Status = PasswordResetRequestStatus.Dismissed;
        request.HandledByUserId = actor.UserId;
        request.HandledAtUtc = businessTime.UtcNow;

        await db.ExecuteInTransactionAsync(async token =>
        {
            await audit.WriteAsync(new AuditEntry(
                "auth", "auth.passwordResetDismissed", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actor.UserId,
                SessionId = actor.SessionId,
                TargetType = "PasswordResetRequest",
                TargetId = request.Id.ToString(),
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);
        return true;
    }
}
