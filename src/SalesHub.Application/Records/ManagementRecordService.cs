using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Records;

/// <summary>
/// Employee management records (CLAUDE.md §13): append-only NOTE entries with
/// follow-ups, manager acknowledgments, validated record links (unlink keeps
/// the row and demands a reason), and one shared tag library. High priority
/// auto-pins and notifies management.
/// </summary>
public sealed class ManagementRecordService(
    IAppDb db,
    IIdentityService identity,
    IPublicIdGenerator publicIds,
    IAuditWriter audit,
    NotificationService notifications,
    BusinessTime businessTime)
{
    public static readonly string[] Categories = ["Attendance", "Coaching", "Technical", "Other"];

    /// <summary>Prefixes a note may link to (CLAUDE.md §13).</summary>
    private static readonly string[] LinkablePrefixes = ["PRS", "BRK", "TECH", "TO", "SCH", "SUP", "NOTE"];

    public sealed record NoteResult(ManagementNote? Note, string? Error)
    {
        public static NoteResult Fail(string error) => new(null, error);
    }

    public async Task<NoteResult> AddNoteAsync(
        Guid actorUserId, Guid employeeUserId, string category,
        ManagementNotePriority priority, string body, bool requireAcknowledgment,
        IReadOnlyList<Guid>? ackTargetUserIds, CancellationToken ct = default)
    {
        if (!Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
        {
            return NoteResult.Fail("Category must be Attendance, Coaching, Technical, or Other.");
        }

        var text = body?.Trim() ?? "";
        if (text.Length is 0 or > 8000)
        {
            return NoteResult.Fail("A note needs text up to 8000 characters.");
        }

        var employee = await identity.FindByIdAsync(employeeUserId, ct);
        if (employee is null)
        {
            return NoteResult.Fail("Employee not found.");
        }

        var ackTargets = ackTargetUserIds?.Distinct().ToList() ?? [];
        if (requireAcknowledgment && ackTargets.Count == 0)
        {
            return NoteResult.Fail("Select at least one manager to acknowledge.");
        }

        if (ackTargets.Count > 0)
        {
            // Acknowledgments are for managers (CLAUDE.md §13).
            var management = (await identity.ListUsersAsync(
                new UserQuery(Roles: Roles.Management), ct)).Select(u => u.Id).ToHashSet();
            if (ackTargets.Any(t => !management.Contains(t)))
            {
                return NoteResult.Fail("Acknowledgment targets must be management users.");
            }
        }

        ManagementNote note = null!;
        await db.ExecuteInTransactionAsync(async token =>
        {
            var now = businessTime.UtcNow;
            note = new ManagementNote
            {
                Id = Guid.CreateVersion7(),
                PublicId = await publicIds.NextAsync("NOTE", token),
                EmployeeUserId = employeeUserId,
                Category = Categories.First(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)),
                Priority = priority,
                Body = text,
                CreatedByUserId = actorUserId,
                CreatedAtUtc = now,
                AcknowledgmentRequired = requireAcknowledgment && ackTargets.Count > 0,
            };

            if (priority == ManagementNotePriority.High)
            {
                // High auto-pins to the top of the employee's record.
                var maxRank = await db.ManagementNotes
                    .Where(n => n.EmployeeUserId == employeeUserId && n.PinnedRank != null)
                    .MaxAsync(n => n.PinnedRank, token) ?? 0;
                note.PinnedRank = maxRank + 1;
            }

            db.ManagementNotes.Add(note);

            foreach (var target in ackTargets)
            {
                db.ManagementNoteAckTargets.Add(new ManagementNoteAckTarget
                {
                    Id = Guid.CreateVersion7(),
                    NoteId = note.Id,
                    TargetUserId = target,
                    RequiredAtUtc = now,
                });

                _ = await notifications.CreateAsync(target,
                    new NotificationService.NewNotification(
                        "records",
                        "Acknowledgment required",
                        $"{note.PublicId}: a management note needs your acknowledgment.",
                        Required: true,
                        ReferenceType: "ManagementNote",
                        ReferenceId: note.PublicId), token);
            }

            if (priority == ManagementNotePriority.High)
            {
                await notifications.CreateForManagementAsync(
                    new NotificationService.NewNotification(
                        "records",
                        "High-priority management note",
                        $"{note.PublicId} ({note.Category}).",
                        ReferenceType: "ManagementNote",
                        ReferenceId: note.PublicId), excludeUserId: actorUserId, ct: token);
            }

            await audit.WriteAsync(new AuditEntry(
                "records", "records.noteCreated", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actorUserId,
                TargetType = "ManagementNote",
                TargetId = note.Id.ToString(),
                PublicRecordId = note.PublicId,
                After = new { employeeUserId, category = note.Category, priority = priority.ToString() },
            }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new NoteResult(note, null);
    }

    public async Task<string?> AddFollowupAsync(
        Guid noteId, Guid actorUserId, string body, CancellationToken ct = default)
    {
        var text = body?.Trim() ?? "";
        if (text.Length is 0 or > 8000)
        {
            return "A follow-up needs text up to 8000 characters.";
        }

        var note = await db.ManagementNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null)
        {
            return "Note not found.";
        }

        db.ManagementNoteFollowups.Add(new ManagementNoteFollowup
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            AuthorUserId = actorUserId,
            Kind = ManagementNoteFollowupKind.Followup,
            Body = text,
            CreatedAtUtc = businessTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> ResolveAsync(
        Guid noteId, Guid actorUserId, string resolutionNote, CancellationToken ct = default)
    {
        var text = resolutionNote?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "A resolution needs a note.";
        }

        var note = await db.ManagementNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null)
        {
            return "Note not found.";
        }

        if (note.Status == ManagementNoteStatus.Resolved)
        {
            return "The note is already resolved.";
        }

        var now = businessTime.UtcNow;
        note.Status = ManagementNoteStatus.Resolved;
        note.ResolvedByUserId = actorUserId;
        note.ResolvedAtUtc = now;
        note.ResolutionNote = text;
        note.PinnedRank = null; // resolution unpins

        db.ManagementNoteFollowups.Add(new ManagementNoteFollowup
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            AuthorUserId = actorUserId,
            Kind = ManagementNoteFollowupKind.Resolution,
            Body = text,
            CreatedAtUtc = now,
        });

        await audit.WriteAsync(new AuditEntry(
            "records", "records.noteResolved", AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "ManagementNote",
            TargetId = note.Id.ToString(),
            PublicRecordId = note.PublicId,
        }, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Reopen requires a reason. The prior resolution stays in the
    /// chronology (its Resolution follow-up is append-only) and the public
    /// record ID is retained (CLAUDE.md §18).</summary>
    public async Task<string?> ReopenAsync(
        Guid noteId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "Reopening needs a reason.";
        }

        var note = await db.ManagementNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null)
        {
            return "Note not found.";
        }

        if (note.Status != ManagementNoteStatus.Resolved)
        {
            return "Only resolved notes can be reopened.";
        }

        note.Status = ManagementNoteStatus.Open;
        note.ResolvedByUserId = null;
        note.ResolvedAtUtc = null;
        note.ResolutionNote = null;

        db.ManagementNoteFollowups.Add(new ManagementNoteFollowup
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            AuthorUserId = actorUserId,
            Kind = ManagementNoteFollowupKind.Reopen,
            Body = text,
            CreatedAtUtc = businessTime.UtcNow,
        });

        await audit.WriteAsync(new AuditEntry(
            "records", "records.noteReopened", AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "ManagementNote",
            TargetId = note.Id.ToString(),
            PublicRecordId = note.PublicId,
            Reason = text,
        }, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> AcknowledgeAsync(
        Guid noteId, Guid actorUserId, CancellationToken ct = default)
    {
        var target = await db.ManagementNoteAckTargets
            .FirstOrDefaultAsync(a => a.NoteId == noteId && a.TargetUserId == actorUserId, ct);
        if (target is null)
        {
            return "You are not an acknowledgment target for this note.";
        }

        if (target.AcknowledgedAtUtc is null)
        {
            target.AcknowledgedAtUtc = businessTime.UtcNow;
            await audit.WriteAsync(new AuditEntry(
                "records", "records.noteAcknowledged", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = actorUserId,
                TargetType = "ManagementNote",
                TargetId = noteId.ToString(),
            }, ct);
            await db.SaveChangesAsync(ct);
        }

        return null;
    }

    // ── record links ──────────────────────────────────────────────────────────

    public async Task<(RecordLink? Link, string? Error)> LinkAsync(
        Guid noteId, Guid actorUserId, string targetPublicId, CancellationToken ct = default)
    {
        var note = await db.ManagementNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null)
        {
            return (null, "Note not found.");
        }

        var target = targetPublicId?.Trim().ToUpperInvariant() ?? "";
        if (!PublicRecordId.IsWellFormed(target)
            || !LinkablePrefixes.Contains(target.Split('-')[0]))
        {
            return (null, "The target must be a PRS/BRK/TECH/TO/SCH/SUP/NOTE record ID.");
        }

        if (!await RecordExistsAsync(target, ct))
        {
            return (null, $"Record {target} does not exist.");
        }

        var duplicate = await db.RecordLinks.AnyAsync(l =>
            l.SourcePublicId == note.PublicId && l.TargetPublicId == target
            && l.RemovedAtUtc == null, ct);
        if (duplicate)
        {
            return (null, "That record is already linked.");
        }

        var link = new RecordLink
        {
            Id = Guid.CreateVersion7(),
            SourcePublicId = note.PublicId,
            TargetPublicId = target,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = businessTime.UtcNow,
        };
        db.RecordLinks.Add(link);
        await audit.WriteAsync(new AuditEntry(
            "records", "records.linked", AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "RecordLink",
            TargetId = link.Id.ToString(),
            PublicRecordId = note.PublicId,
            After = new { target },
        }, ct);
        await db.SaveChangesAsync(ct);
        return (link, null);
    }

    /// <summary>Unlink keeps the row: removed-by, removed-at, and the required
    /// reason stay part of the record's history.</summary>
    public async Task<string?> UnlinkAsync(
        Guid linkId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "Unlinking needs a reason.";
        }

        var link = await db.RecordLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.RemovedAtUtc == null, ct);
        if (link is null)
        {
            return "Active link not found.";
        }

        link.RemovedByUserId = actorUserId;
        link.RemovedAtUtc = businessTime.UtcNow;
        link.RemoveReason = text;
        await audit.WriteAsync(new AuditEntry(
            "records", "records.unlinked", AuditRetentionClass.AccountLifetime)
        {
            ActorUserId = actorUserId,
            TargetType = "RecordLink",
            TargetId = link.Id.ToString(),
            PublicRecordId = link.SourcePublicId,
            Reason = text,
        }, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private async Task<bool> RecordExistsAsync(string publicId, CancellationToken ct) =>
        publicId.Split('-')[0] switch
        {
            "PRS" => await db.PresenceFlags.AnyAsync(x => x.PublicId == publicId, ct),
            "BRK" => await db.BreakCorrectionRequests.AnyAsync(x => x.PublicId == publicId, ct),
            "TECH" => await db.TechnicalReports.AnyAsync(x => x.PublicId == publicId, ct),
            "TO" => await db.TimeOffRequests.AnyAsync(x => x.PublicId == publicId, ct),
            "SCH" => await db.ScheduleExceptions.AnyAsync(x => x.PublicId == publicId, ct),
            "SUP" => await db.SupportTickets.AnyAsync(x => x.PublicId == publicId, ct),
            "NOTE" => await db.ManagementNotes.AnyAsync(x => x.PublicId == publicId, ct),
            _ => false,
        };

    // ── tags ──────────────────────────────────────────────────────────────────

    public async Task<(ManagementTag? Tag, string? Error)> CreateTagAsync(
        Guid actorUserId, string label, CancellationToken ct = default)
    {
        var text = label?.Trim() ?? "";
        if (text.Length is 0 or > 64)
        {
            return (null, "A tag needs a label up to 64 characters.");
        }

        if (await db.ManagementTags.AnyAsync(t => t.Label == text, ct))
        {
            return (null, $"Tag '{text}' already exists.");
        }

        var tag = new ManagementTag
        {
            Id = Guid.CreateVersion7(),
            Label = text,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = businessTime.UtcNow,
        };
        db.ManagementTags.Add(tag);
        await db.SaveChangesAsync(ct);
        return (tag, null);
    }

    public async Task<string?> TagAsync(
        Guid tagId, Guid actorUserId, string entityPublicId, CancellationToken ct = default)
    {
        var tag = await db.ManagementTags.FirstOrDefaultAsync(t => t.Id == tagId && t.Active, ct);
        if (tag is null)
        {
            return "Tag not found.";
        }

        var target = entityPublicId?.Trim().ToUpperInvariant() ?? "";
        if (!PublicRecordId.IsWellFormed(target) || !await RecordExistsAsync(target, ct))
        {
            return "The tagged record must exist.";
        }

        var exists = await db.TaggedEntities
            .AnyAsync(t => t.TagId == tagId && t.EntityPublicId == target, ct);
        if (!exists)
        {
            db.TaggedEntities.Add(new TaggedEntity
            {
                Id = Guid.CreateVersion7(),
                TagId = tagId,
                EntityPublicId = target,
                CreatedByUserId = actorUserId,
                CreatedAtUtc = businessTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return null;
    }

    public async Task<string?> UntagAsync(
        Guid tagId, string entityPublicId, CancellationToken ct = default)
    {
        var target = entityPublicId?.Trim().ToUpperInvariant() ?? "";
        var row = await db.TaggedEntities
            .FirstOrDefaultAsync(t => t.TagId == tagId && t.EntityPublicId == target, ct);
        if (row is null)
        {
            return "That tag is not applied to the record.";
        }

        db.TaggedEntities.Remove(row);
        await db.SaveChangesAsync(ct);
        return null;
    }
}
