using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Support;

/// <summary>
/// Support tickets (CLAUDE.md §14, SUP-YYYY-#####). Context is captured
/// server-side at creation; the system suggests a priority that management
/// can override; internal notes are a separate visibility class the reporter
/// never receives; Critical pushes all of management.
/// </summary>
public sealed class SupportService(
    IAppDb db,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    NotificationService notifications,
    ICorrelationAccessor correlation,
    BusinessTime businessTime)
{
    private static readonly string[] IssueTypes =
        ["Internet", "Computer", "BrowserPwa", "Sales", "Presence", "Account", "Other"];

    private static readonly string[] CriticalHints =
        ["cannot log in", "can't log in", "locked out", "nobody can", "everyone", "all agents", "data loss"];

    private static readonly string[] HighHints =
        ["crash", "frozen", "stuck", "cannot save", "can't save", "error every"];

    public sealed record TicketResult(
        SupportTicket? Ticket, string? Error, IReadOnlyList<SupportTicket>? Similar = null)
    {
        public static TicketResult Fail(string error) => new(null, error);
    }

    public async Task<TicketResult> CreateAsync(
        Guid reporterUserId, UserSession session, string issueType, string description,
        string? page, string? appVersion, Guid? attachmentBlobId, CancellationToken ct = default)
    {
        if (!IssueTypes.Contains(issueType, StringComparer.OrdinalIgnoreCase))
        {
            return TicketResult.Fail(
                "Issue type must be one of: " + string.Join(", ", IssueTypes) + ".");
        }

        var text = description?.Trim() ?? "";
        if (text.Length is 0 or > 8000)
        {
            return TicketResult.Fail("A ticket needs a description up to 8000 characters.");
        }

        if (attachmentBlobId is { } blobId
            && !await db.FileBlobs.AnyAsync(b => b.Id == blobId, ct))
        {
            return TicketResult.Fail("Attachment not found.");
        }

        var (suggested, reason) = SuggestPriority(text);

        SupportTicket ticket = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            ticket = new SupportTicket
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("SUP", token),
                ReporterUserId = reporterUserId,
                IssueType = IssueTypes.First(t => t.Equals(issueType, StringComparison.OrdinalIgnoreCase)),
                Description = text,
                Page = page?.Trim() ?? "",
                // Context capture is server-side (CLAUDE.md §14).
                AppVersion = appVersion?.Trim() ?? session.AppVersion,
                BrowserFamily = session.BrowserFamily,
                DeviceId = session.DeviceId,
                CorrelationId = correlation.CorrelationId,
                Priority = suggested,
                SuggestedPriority = suggested,
                SuggestedPriorityReason = reason,
                CreatedAtUtc = businessTime.UtcNow,
            };
            db.SupportTickets.Add(ticket);

            if (attachmentBlobId is { } blob)
            {
                db.SupportAttachments.Add(new SupportAttachment
                {
                    Id = Guid.CreateVersion7(),
                    TicketId = ticket.Id,
                    BlobId = blob,
                    UploadedByUserId = reporterUserId,
                    CreatedAtUtc = ticket.CreatedAtUtc,
                });
            }

            await audit.WriteAsync(new AuditEntry(
                "support", "support.ticketCreated", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = reporterUserId,
                TargetType = "SupportTicket",
                TargetId = ticket.Id.ToString(),
                PublicRecordId = ticket.PublicId,
                After = new { issueType = ticket.IssueType, priority = ticket.Priority.ToString() },
            }, token);

            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "support",
                suggested == SupportPriority.Critical
                    ? "CRITICAL support ticket"
                    : "New support ticket",
                $"{ticket.PublicId}: {ticket.IssueType}.",
                ReferenceType: "SupportTicket",
                ReferenceId: ticket.PublicId), excludeUserId: reporterUserId, ct: token);

            await db.SaveChangesAsync(token);
        }, ct);

        var similar = await FindSimilarAsync(ticket, ct);
        return new TicketResult(ticket, null, similar);
    }

    /// <summary>Similar-ticket detection (CLAUDE.md §14): recent tickets with
    /// the same issue type, newest first — surfaced, never auto-merged.</summary>
    public async Task<IReadOnlyList<SupportTicket>> FindSimilarAsync(
        SupportTicket ticket, CancellationToken ct = default)
    {
        var horizon = businessTime.UtcNow - TimeSpan.FromDays(14);
        return await db.SupportTickets
            .Where(t => t.Id != ticket.Id
                && t.IssueType == ticket.IssueType
                && t.CreatedAtUtc >= horizon
                && t.Status != SupportTicketStatus.Closed)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(5)
            .ToListAsync(ct);
    }

    private static (SupportPriority Priority, string? Reason) SuggestPriority(string description)
    {
        var lowered = description.ToLowerInvariant();
        if (CriticalHints.Any(lowered.Contains))
        {
            return (SupportPriority.Critical, "Description suggests a widespread or blocking outage.");
        }

        if (HighHints.Any(lowered.Contains))
        {
            return (SupportPriority.High, "Description suggests the app is unusable for this user.");
        }

        return (SupportPriority.Normal, null);
    }

    public async Task<string?> ReplyAsync(
        Guid ticketId, Guid authorUserId, bool authorIsManagement,
        string body, SupportMessageVisibility visibility, CancellationToken ct = default)
    {
        var text = body?.Trim() ?? "";
        if (text.Length is 0 or > 8000)
        {
            return "A reply needs text up to 8000 characters.";
        }

        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        if (!authorIsManagement)
        {
            if (ticket.ReporterUserId != authorUserId)
            {
                return "Ticket not found."; // no existence oracle across reporters
            }

            if (visibility == SupportMessageVisibility.InternalNote)
            {
                return "Internal notes are management-only.";
            }
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.SupportMessages.Add(new SupportMessage
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticket.Id,
                AuthorUserId = authorUserId,
                Visibility = visibility,
                Body = text,
                CreatedAtUtc = businessTime.UtcNow,
            });

            // Employee-visible replies move the conversation state; internal
            // notes never do.
            if (visibility == SupportMessageVisibility.EmployeeReply)
            {
                if (authorIsManagement && ticket.Status is SupportTicketStatus.Open
                    or SupportTicketStatus.InProgress)
                {
                    ticket.Status = SupportTicketStatus.WaitingOnUser;
                }
                else if (!authorIsManagement
                    && ticket.Status == SupportTicketStatus.WaitingOnUser)
                {
                    ticket.Status = SupportTicketStatus.InProgress;
                }

                if (authorIsManagement)
                {
                    _ = await notifications.CreateAsync(ticket.ReporterUserId,
                        new NotificationService.NewNotification(
                            "support",
                            "Support replied",
                            $"{ticket.PublicId}: new reply from support.",
                            ReferenceType: "SupportTicket",
                            ReferenceId: ticket.PublicId), token);
                }
                else
                {
                    await notifications.CreateForManagementAsync(
                        new NotificationService.NewNotification(
                            "support",
                            "Support ticket updated",
                            $"{ticket.PublicId}: the reporter replied.",
                            ReferenceType: "SupportTicket",
                            ReferenceId: ticket.PublicId), excludeUserId: authorUserId, ct: token);
                }
            }

            await db.SaveChangesAsync(token);
        }, ct);

        return null;
    }

    public async Task<string?> AssignAsync(
        Guid ticketId, Guid actorUserId, Guid primaryAssigneeUserId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        ticket.PrimaryAssigneeUserId = primaryAssigneeUserId;
        if (ticket.Status == SupportTicketStatus.Open)
        {
            ticket.Status = SupportTicketStatus.InProgress;
        }

        _ = await notifications.CreateAsync(primaryAssigneeUserId,
            new NotificationService.NewNotification(
                "support",
                "Support ticket assigned to you",
                $"{ticket.PublicId}: {ticket.IssueType}.",
                ReferenceType: "SupportTicket",
                ReferenceId: ticket.PublicId), ct);
        await audit.WriteAsync(new AuditEntry(
            "support", "support.assigned", AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "SupportTicket",
            TargetId = ticket.Id.ToString(),
            PublicRecordId = ticket.PublicId,
            After = new { primaryAssigneeUserId },
        }, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> AddCollaboratorAsync(
        Guid ticketId, Guid actorUserId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        var exists = await db.SupportCollaborators
            .AnyAsync(c => c.TicketId == ticketId && c.UserId == userId, ct);
        if (!exists)
        {
            db.SupportCollaborators.Add(new SupportCollaborator
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticketId,
                UserId = userId,
                AddedByUserId = actorUserId,
                AddedAtUtc = businessTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return null;
    }

    /// <summary>Management priority override; the suggestion stays recorded.</summary>
    public async Task<string?> SetPriorityAsync(
        Guid ticketId, Guid actorUserId, SupportPriority priority, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        var before = ticket.Priority;
        ticket.Priority = priority;
        await audit.WriteAsync(new AuditEntry(
            "support", "support.priorityChanged", AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "SupportTicket",
            TargetId = ticket.Id.ToString(),
            PublicRecordId = ticket.PublicId,
            Before = new { priority = before.ToString() },
            After = new { priority = priority.ToString() },
        }, ct);

        if (priority == SupportPriority.Critical && before != SupportPriority.Critical)
        {
            await notifications.CreateForManagementAsync(new NotificationService.NewNotification(
                "support",
                "CRITICAL support ticket",
                $"{ticket.PublicId}: escalated to Critical.",
                ReferenceType: "SupportTicket",
                ReferenceId: ticket.PublicId), excludeUserId: actorUserId, ct: ct);
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> ResolveAsync(
        Guid ticketId, Guid actorUserId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        if (ticket.Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
        {
            return "The ticket is already resolved.";
        }

        ticket.Status = SupportTicketStatus.Resolved;
        ticket.ResolvedAtUtc = businessTime.UtcNow;
        _ = await notifications.CreateAsync(ticket.ReporterUserId,
            new NotificationService.NewNotification(
                "support",
                "Support ticket resolved",
                $"{ticket.PublicId}: confirm to close it, or reply to reopen.",
                ReferenceType: "SupportTicket",
                ReferenceId: ticket.PublicId), ct);
        await WriteStatusAuditAsync(ticket, actorUserId, "support.resolved", ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>The reporter's confirmation after Resolved closes the ticket.</summary>
    public async Task<string?> ConfirmClosureAsync(
        Guid ticketId, Guid reporterUserId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(
            t => t.Id == ticketId && t.ReporterUserId == reporterUserId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        if (ticket.Status != SupportTicketStatus.Resolved)
        {
            return "Only resolved tickets can be confirmed closed.";
        }

        ticket.Status = SupportTicketStatus.Closed;
        ticket.ClosedAtUtc = businessTime.UtcNow;
        ticket.ClosedByUserId = reporterUserId;
        ticket.ReporterConfirmedClosure = true;
        await WriteStatusAuditAsync(ticket, reporterUserId, "support.closedByReporter", ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Management force close, at any state.</summary>
    public async Task<string?> ForceCloseAsync(
        Guid ticketId, Guid actorUserId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        if (ticket.Status == SupportTicketStatus.Closed)
        {
            return "The ticket is already closed.";
        }

        ticket.Status = SupportTicketStatus.Closed;
        ticket.ClosedAtUtc = businessTime.UtcNow;
        ticket.ClosedByUserId = actorUserId;
        ticket.ForceClosed = true;
        await WriteStatusAuditAsync(ticket, actorUserId, "support.forceClosed", ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Reopen keeps the original SUP id (CLAUDE.md §18).</summary>
    public async Task<string?> ReopenAsync(
        Guid ticketId, Guid actorUserId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        if (ticket.Status is not (SupportTicketStatus.Resolved or SupportTicketStatus.Closed))
        {
            return "Only resolved or closed tickets can be reopened.";
        }

        ticket.Status = SupportTicketStatus.InProgress;
        ticket.ResolvedAtUtc = null;
        ticket.ClosedAtUtc = null;
        ticket.ClosedByUserId = null;
        ticket.ForceClosed = false;
        ticket.ReporterConfirmedClosure = false;
        await WriteStatusAuditAsync(ticket, actorUserId, "support.reopened", ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> LinkAsync(
        Guid ticketId, Guid actorUserId, string targetPublicId, CancellationToken ct = default)
    {
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return "Ticket not found.";
        }

        var target = targetPublicId?.Trim().ToUpperInvariant() ?? "";
        if (!PublicRecordId.IsWellFormed(target))
        {
            return "The target must be a public record ID.";
        }

        var exists = await db.SupportLinks
            .AnyAsync(l => l.TicketId == ticketId && l.TargetPublicId == target, ct);
        if (!exists)
        {
            db.SupportLinks.Add(new SupportLink
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticketId,
                TargetPublicId = target,
                CreatedByUserId = actorUserId,
                CreatedAtUtc = businessTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return null;
    }

    private Task WriteStatusAuditAsync(
        SupportTicket ticket, Guid actorUserId, string action, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(
            "support", action, AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "SupportTicket",
            TargetId = ticket.Id.ToString(),
            PublicRecordId = ticket.PublicId,
            After = new { status = ticket.Status.ToString() },
        }, ct);
}
