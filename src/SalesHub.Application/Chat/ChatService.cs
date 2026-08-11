using Microsoft.EntityFrameworkCore;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Notifications;
using SalesHub.Contracts.Events;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Application.Chat;

/// <summary>
/// Chat rules (CLAUDE.md §7). Everyone DMs anyone; only management creates
/// or edits groups; members never self-leave; mandatory groups refuse
/// muting; @everyone is management-only; message deletion erases the body
/// from canonical state with no hidden copy. Realtime rides the outbox with
/// explicit member routing.
/// </summary>
public sealed class ChatService(
    IAppDb db,
    IIdentityService identity,
    NotificationService notifications,
    IAuditWriter audit,
    IOutboxWriter outbox,
    BusinessTime businessTime)
{
    public enum Failure { None, NotFound, Forbidden, Validation }

    public sealed record Outcome<T>(Failure Failure, string? Error, T? Value) where T : class
    {
        public static Outcome<T> Ok(T value) => new(Failure.None, null, value);
        public static Outcome<T> Fail(Failure failure, string error) => new(failure, error, null);
    }

    // ── conversations ────────────────────────────────────────────────────────

    /// <summary>Get-or-create the DM between the caller and another active user.</summary>
    public async Task<Outcome<Conversation>> StartDirectAsync(
        Guid callerId, Guid otherUserId, CancellationToken ct = default)
    {
        if (callerId == otherUserId)
        {
            return Outcome<Conversation>.Fail(Failure.Validation, "You cannot DM yourself.");
        }

        var other = await identity.FindByIdAsync(otherUserId, ct);
        if (other is null || !other.IsActive)
        {
            return Outcome<Conversation>.Fail(Failure.NotFound, "That user is not available.");
        }

        var key = Conversation.MakeDirectKey(callerId, otherUserId);
        var existing = await db.Conversations.FirstOrDefaultAsync(c => c.DirectKey == key, ct);
        if (existing is not null)
        {
            return Outcome<Conversation>.Ok(existing);
        }

        var now = businessTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            Type = ConversationType.Direct,
            DirectKey = key,
            CreatedByUserId = callerId,
            CreatedAtUtc = now,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Conversations.Add(conversation);
            db.ConversationMembers.AddRange(
                new ConversationMember { ConversationId = conversation.Id, UserId = callerId, JoinedAtUtc = now },
                new ConversationMember { ConversationId = conversation.Id, UserId = otherUserId, JoinedAtUtc = now });
            await EmitConversationChangedAsync(conversation.Id, [callerId, otherUserId], token);
            await db.SaveChangesAsync(token);
        }, ct);

        return Outcome<Conversation>.Ok(conversation);
    }

    public async Task<Outcome<Conversation>> CreateGroupAsync(
        Guid actorId, string name, IReadOnlyList<Guid> memberIds, bool mandatory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Outcome<Conversation>.Fail(Failure.Validation, "A group needs a name.");
        }

        var now = businessTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            Type = ConversationType.Group,
            Name = name.Trim(),
            Mandatory = mandatory,
            CreatedByUserId = actorId,
            CreatedAtUtc = now,
        };

        var members = memberIds.Append(actorId).Distinct().ToList();
        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Conversations.Add(conversation);
            db.ConversationMembers.AddRange(members.Select(id => new ConversationMember
            {
                ConversationId = conversation.Id,
                UserId = id,
                JoinedAtUtc = now,
            }));
            await audit.WriteAsync(new AuditEntry("chat", "chat.groupCreated", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorId,
                TargetType = "Conversation",
                TargetId = conversation.Id.ToString(),
                After = new { name = conversation.Name, mandatory, members },
            }, token);
            await EmitConversationChangedAsync(conversation.Id, members, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return Outcome<Conversation>.Ok(conversation);
    }

    public async Task<Outcome<Conversation>> UpdateGroupAsync(
        Guid actorId, Guid conversationId, string? name, bool? mandatory,
        IReadOnlyList<Guid>? add, IReadOnlyList<Guid>? remove,
        CancellationToken ct = default)
    {
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.Type == ConversationType.Group, ct);
        if (conversation is null)
        {
            return Outcome<Conversation>.Fail(Failure.NotFound, "Group not found.");
        }

        var now = businessTime.UtcNow;
        await db.ExecuteInTransactionAsync(async token =>
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                conversation.Name = name.Trim();
            }

            if (mandatory is { } m)
            {
                conversation.Mandatory = m;
                if (m)
                {
                    // A newly-mandatory group clears existing mutes.
                    await db.ConversationMembers
                        .Where(x => x.ConversationId == conversationId && x.MutedAtUtc != null)
                        .ExecuteUpdateAsync(s => s.SetProperty(
                            x => x.MutedAtUtc, (DateTimeOffset?)null), token);
                }
            }

            var current = await db.ConversationMembers
                .Where(m2 => m2.ConversationId == conversationId)
                .Select(m2 => m2.UserId)
                .ToListAsync(token);

            foreach (var userId in (add ?? []).Except(current))
            {
                db.ConversationMembers.Add(new ConversationMember
                {
                    ConversationId = conversationId,
                    UserId = userId,
                    JoinedAtUtc = now,
                });
            }

            var removals = (remove ?? []).Intersect(current).ToList();
            if (removals.Count > 0)
            {
                await db.ConversationMembers
                    .Where(m2 => m2.ConversationId == conversationId && removals.Contains(m2.UserId))
                    .ExecuteDeleteAsync(token);
            }

            await audit.WriteAsync(new AuditEntry("chat", "chat.groupUpdated", AuditRetentionClass.Operational365Days)
            {
                ActorUserId = actorId,
                TargetType = "Conversation",
                TargetId = conversationId.ToString(),
                After = new { name, mandatory, add, remove },
            }, token);

            var futureMembers = current.Except(removals).Union(add ?? []).Distinct().ToList();
            await EmitConversationChangedAsync(conversationId, futureMembers, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return Outcome<Conversation>.Ok(conversation);
    }

    public async Task<Failure> SetMutedAsync(
        Guid userId, Guid conversationId, bool muted, CancellationToken ct = default)
    {
        var member = await db.ConversationMembers
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct);
        if (member is null)
        {
            return Failure.NotFound;
        }

        if (muted)
        {
            var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId, ct);
            if (conversation.Mandatory)
            {
                return Failure.Forbidden; // mandatory groups cannot be muted
            }
        }

        member.MutedAtUtc = muted ? businessTime.UtcNow : null;
        await db.SaveChangesAsync(ct);
        return Failure.None;
    }

    // ── messages ─────────────────────────────────────────────────────────────

    public sealed record SendInput(
        Guid ConversationId,
        Guid SenderUserId,
        string SenderRole,
        string Body,
        Guid? ReplyToMessageId,
        IReadOnlyList<Guid> MentionedUserIds,
        bool MentionEveryone,
        IReadOnlyList<Guid> AttachmentBlobIds);

    public async Task<Outcome<Message>> SendAsync(SendInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Body) && input.AttachmentBlobIds.Count == 0)
        {
            return Outcome<Message>.Fail(Failure.Validation, "A message needs text or an attachment.");
        }

        if (input.MentionEveryone && !Roles.IsManagement(input.SenderRole))
        {
            // @everyone is management announcement behavior (CLAUDE.md §7).
            return Outcome<Message>.Fail(Failure.Forbidden,
                "@everyone is reserved for management.");
        }

        var members = await MemberIdsAsync(input.ConversationId, ct);
        if (!members.Contains(input.SenderUserId))
        {
            return Outcome<Message>.Fail(Failure.Forbidden, "You are not in this conversation.");
        }

        var mentioned = input.MentionEveryone
            ? members.Where(id => id != input.SenderUserId).ToList()
            : input.MentionedUserIds.Where(id => members.Contains(id) && id != input.SenderUserId)
                .Distinct().ToList();

        var conversation = await db.Conversations.FirstAsync(c => c.Id == input.ConversationId, ct);
        var sender = await identity.FindByIdAsync(input.SenderUserId, ct);
        var now = businessTime.UtcNow;
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = input.ConversationId,
            SenderUserId = input.SenderUserId,
            Body = input.Body.Trim(),
            CreatedAtUtc = now,
            ReplyToMessageId = input.ReplyToMessageId,
        };

        await db.ExecuteInTransactionAsync(async token =>
        {
            db.Messages.Add(message);

            foreach (var blobId in input.AttachmentBlobIds.Distinct())
            {
                var blob = await db.FileBlobs.FirstOrDefaultAsync(b => b.Id == blobId, token)
                    ?? throw new InvalidOperationException("Unknown attachment blob.");
                db.MessageAttachments.Add(new MessageAttachment
                {
                    Id = Guid.CreateVersion7(),
                    MessageId = message.Id,
                    BlobId = blob.Id,
                    OriginalName = blob.OriginalName,
                    ContentType = blob.ContentType,
                    ByteLength = blob.ByteLength,
                });
            }

            await outbox.EnqueueAsync(EventTypes.MessageCreated, new
            {
                memberUserIds = members,
                conversationId = input.ConversationId,
                messageId = message.Id,
                senderUserId = input.SenderUserId,
                senderDisplayName = sender?.DisplayName ?? "",
                body = message.Body,
                createdAt = message.CreatedAtUtc,
            }, token);

            // Durable notifications: DM recipients always; in groups, only
            // mentions (mute-override per CLAUDE.md §7 — ordinary group
            // traffic is unread-badge territory, not Notification Center).
            var notifyTargets = conversation.Type == ConversationType.Direct
                ? members.Where(id => id != input.SenderUserId)
                : mentioned;
            foreach (var target in notifyTargets)
            {
                _ = await notifications.CreateAsync(target, new NotificationService.NewNotification(
                    "chat",
                    conversation.Type == ConversationType.Direct
                        ? $"Message from {sender?.DisplayName}"
                        : $"{sender?.DisplayName} mentioned you in {conversation.Name}",
                    SafePreviewOf(message.Body),
                    ReferenceType: "Conversation",
                    ReferenceId: conversation.Id.ToString()), token);
            }

            await db.SaveChangesAsync(token);
        }, ct);

        return Outcome<Message>.Ok(message);
    }

    public async Task<Outcome<Message>> EditAsync(
        Guid messageId, Guid actorId, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Outcome<Message>.Fail(Failure.Validation, "A message needs text.");
        }

        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.DeletedAtUtc is not null)
        {
            return Outcome<Message>.Fail(Failure.NotFound, "Message not found.");
        }

        if (message.SenderUserId != actorId)
        {
            return Outcome<Message>.Fail(Failure.Forbidden, "You can only edit your own messages.");
        }

        var members = await MemberIdsAsync(message.ConversationId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            message.Body = body.Trim();
            message.EditedAtUtc = businessTime.UtcNow;   // clients render "Edited"
            await outbox.EnqueueAsync(EventTypes.MessageEdited, new
            {
                memberUserIds = members,
                conversationId = message.ConversationId,
                messageId = message.Id,
                body = message.Body,
                editedAt = message.EditedAtUtc,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return Outcome<Message>.Ok(message);
    }

    /// <summary>Deletes own message: the body is erased in place — canonical
    /// state keeps only the shell. No audit copy of the content exists.</summary>
    public async Task<Outcome<Message>> DeleteAsync(
        Guid messageId, Guid actorId, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.DeletedAtUtc is not null)
        {
            return Outcome<Message>.Fail(Failure.NotFound, "Message not found.");
        }

        if (message.SenderUserId != actorId)
        {
            return Outcome<Message>.Fail(Failure.Forbidden, "You can only delete your own messages.");
        }

        var members = await MemberIdsAsync(message.ConversationId, ct);
        await db.ExecuteInTransactionAsync(async token =>
        {
            message.Body = string.Empty;                 // content gone, everywhere
            message.DeletedAtUtc = businessTime.UtcNow;
            await db.MessageAttachments
                .Where(a => a.MessageId == message.Id)
                .ExecuteDeleteAsync(token);
            await outbox.EnqueueAsync(EventTypes.MessageDeleted, new
            {
                memberUserIds = members,
                conversationId = message.ConversationId,
                messageId = message.Id,
            }, token);
            await db.SaveChangesAsync(token);
        }, ct);

        return Outcome<Message>.Ok(message);
    }

    public async Task<Failure> ReactAsync(
        Guid messageId, Guid userId, string reaction, bool add, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reaction) || reaction.Length > 32)
        {
            return Failure.Validation;
        }

        var message = await db.Messages.FirstOrDefaultAsync(
            m => m.Id == messageId && m.DeletedAtUtc == null, ct);
        if (message is null)
        {
            return Failure.NotFound;
        }

        var members = await MemberIdsAsync(message.ConversationId, ct);
        if (!members.Contains(userId))
        {
            return Failure.Forbidden;
        }

        var existing = await db.MessageReactions.FirstOrDefaultAsync(
            r => r.MessageId == messageId && r.UserId == userId && r.Reaction == reaction, ct);

        if (add && existing is null)
        {
            db.MessageReactions.Add(new MessageReaction
            {
                MessageId = messageId,
                UserId = userId,
                Reaction = reaction,
                CreatedAtUtc = businessTime.UtcNow,
            });
        }
        else if (!add && existing is not null)
        {
            db.MessageReactions.Remove(existing);
        }
        else
        {
            return Failure.None; // idempotent
        }

        await outbox.EnqueueAsync(EventTypes.ReactionChanged, new
        {
            memberUserIds = members,
            conversationId = message.ConversationId,
            messageId,
            reaction,
            userId,
            added = add,
        }, ct);
        await db.SaveChangesAsync(ct);
        return Failure.None;
    }

    public async Task<Failure> MarkReadAsync(
        Guid conversationId, Guid userId, Guid messageId, CancellationToken ct = default)
    {
        var member = await db.ConversationMembers.FirstOrDefaultAsync(
            m => m.ConversationId == conversationId && m.UserId == userId, ct);
        if (member is null)
        {
            return Failure.NotFound;
        }

        var exists = await db.Messages.AnyAsync(
            m => m.Id == messageId && m.ConversationId == conversationId, ct);
        if (!exists)
        {
            return Failure.NotFound;
        }

        member.LastReadMessageId = messageId;
        member.LastReadAtUtc = businessTime.UtcNow;

        var members = await MemberIdsAsync(conversationId, ct);
        await outbox.EnqueueAsync(EventTypes.ReadPositionChanged, new
        {
            memberUserIds = members,
            conversationId,
            userId,
            messageId,
        }, ct);
        await db.SaveChangesAsync(ct);
        return Failure.None;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    public async Task<List<Guid>> MemberIdsAsync(Guid conversationId, CancellationToken ct = default) =>
        await db.ConversationMembers
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    private async Task EmitConversationChangedAsync(
        Guid conversationId, IReadOnlyList<Guid> members, CancellationToken ct) =>
        await outbox.EnqueueAsync(EventTypes.ConversationChanged, new
        {
            memberUserIds = members,
            conversationId,
        }, ct);

    /// <summary>Lock-screen-safe preview (docs/03): trimmed, never full content.</summary>
    private static string SafePreviewOf(string body) =>
        body.Length <= 80 ? body : body[..77] + "…";
}
