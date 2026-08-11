using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Chat;
using SalesHub.Contracts.Chat;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class ChatEndpoints
{
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;
    private static readonly HashSet<string> AttachmentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder api)
    {
        var chat = api.MapGroup("/conversations").RequireAuthorization(Policies.Employee);
        chat.MapGet("/", ListAsync);
        chat.MapGet("/{id:guid}/messages", MessagesAsync);
        chat.MapPost("/direct", StartDirectAsync);
        chat.MapPost("/groups", CreateGroupAsync).RequireAuthorization(Policies.Management);
        chat.MapPatch("/groups/{id:guid}", UpdateGroupAsync).RequireAuthorization(Policies.Management);
        chat.MapPost("/{id:guid}/messages", SendAsync);
        chat.MapPost("/{id:guid}/read", MarkReadAsync);
        chat.MapPost("/{id:guid}/mute", (Guid id, HttpContext http, ChatService svc, CancellationToken ct) =>
            MuteAsync(id, http, svc, true, ct));
        chat.MapDelete("/{id:guid}/mute", (Guid id, HttpContext http, ChatService svc, CancellationToken ct) =>
            MuteAsync(id, http, svc, false, ct));
        chat.MapPost("/{id:guid}/attachments", UploadAttachmentAsync).DisableAntiforgery();

        var messages = api.MapGroup("/messages").RequireAuthorization(Policies.Employee);
        messages.MapPatch("/{id:guid}", EditAsync);
        messages.MapDelete("/{id:guid}", DeleteAsync);
        messages.MapPost("/{id:guid}/reactions", (
                Guid id, ReactionRequest request, HttpContext http, ChatService svc, CancellationToken ct) =>
            ReactAsync(id, request.Reaction, http, svc, true, ct));
        messages.MapDelete("/{id:guid}/reactions/{reaction}", (
                Guid id, string reaction, HttpContext http, ChatService svc, CancellationToken ct) =>
            ReactAsync(id, reaction, http, svc, false, ct));

        return api;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var memberships = await db.ConversationMembers
            .Where(m => m.UserId == userId)
            .ToListAsync(ct);
        var conversationIds = memberships.Select(m => m.ConversationId).ToList();

        var conversations = await db.Conversations
            .Where(c => conversationIds.Contains(c.Id) && c.Active)
            .ToListAsync(ct);
        var allMembers = await db.ConversationMembers
            .Where(m => conversationIds.Contains(m.ConversationId))
            .ToListAsync(ct);
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var result = new List<ConversationDto>();
        foreach (var conversation in conversations)
        {
            var mine = memberships.First(m => m.ConversationId == conversation.Id);
            var members = allMembers.Where(m => m.ConversationId == conversation.Id).ToList();

            var lastMessage = await db.Messages
                .Where(m => m.ConversationId == conversation.Id)
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync(ct);

            var unread = await db.Messages.CountAsync(m =>
                m.ConversationId == conversation.Id
                && m.SenderUserId != userId
                && m.DeletedAtUtc == null
                && (mine.LastReadMessageId == null
                    || m.Id.CompareTo(mine.LastReadMessageId.Value) > 0), ct);

            var displayName = conversation.Type == ConversationType.Group
                ? conversation.Name
                : users.GetValueOrDefault(
                    members.First(m => m.UserId != userId).UserId, "Unknown");

            result.Add(new ConversationDto(
                conversation.Id,
                conversation.Type.ToString(),
                displayName,
                conversation.Mandatory,
                mine.MutedAtUtc is not null,
                unread,
                lastMessage is null ? null : await ToDtoAsync(lastMessage, db, users, ct),
                members.Select(m => new MemberDto(
                    m.UserId, users.GetValueOrDefault(m.UserId, "Unknown"),
                    m.LastReadMessageId)).ToList()));
        }

        return Results.Ok(result
            .OrderByDescending(c => c.LastMessage?.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList());
    }

    private static async Task<IResult> MessagesAsync(
        Guid id, HttpContext http, IAppDb db, IIdentityService identity,
        Guid? before, int? limit, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var isMember = await db.ConversationMembers
            .AnyAsync(m => m.ConversationId == id && m.UserId == userId, ct);
        if (!isMember)
        {
            return Problems.Forbidden(http, "You are not in this conversation.");
        }

        var pageSize = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Messages.Where(m => m.ConversationId == id);
        if (before is { } cursor)
        {
            query = query.Where(m => m.Id.CompareTo(cursor) < 0);
        }

        var page = await query.OrderByDescending(m => m.Id).Take(pageSize).ToListAsync(ct);
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var dtos = new List<MessageDto>(page.Count);
        foreach (var message in page)
        {
            dtos.Add(await ToDtoAsync(message, db, users, ct));
        }

        return Results.Ok(new MessagePageResponse(
            dtos, page.Count == pageSize ? page[^1].Id : null));
    }

    private static async Task<IResult> StartDirectAsync(
        StartDirectRequest request, HttpContext http, ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var outcome = await chatService.StartDirectAsync(userId, request.UserId, ct);
        return outcome.Failure switch
        {
            ChatService.Failure.None => Results.Ok(new { conversationId = outcome.Value!.Id }),
            ChatService.Failure.NotFound => Problems.NotFound(http, outcome.Error!),
            _ => Problems.Validation(http, outcome.Error!),
        };
    }

    private static async Task<IResult> CreateGroupAsync(
        CreateGroupRequest request, HttpContext http, ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var outcome = await chatService.CreateGroupAsync(
            userId, request.Name, request.MemberUserIds, request.Mandatory, ct);
        return outcome.Failure == ChatService.Failure.None
            ? Results.Created($"/api/v1/conversations/{outcome.Value!.Id}",
                new { conversationId = outcome.Value.Id })
            : Problems.Validation(http, outcome.Error!);
    }

    private static async Task<IResult> UpdateGroupAsync(
        Guid id, UpdateGroupRequest request, HttpContext http,
        ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var outcome = await chatService.UpdateGroupAsync(
            userId, id, request.Name, request.Mandatory,
            request.AddMemberUserIds, request.RemoveMemberUserIds, ct);
        return outcome.Failure switch
        {
            ChatService.Failure.None => Results.NoContent(),
            ChatService.Failure.NotFound => Problems.NotFound(http, outcome.Error!),
            _ => Problems.Validation(http, outcome.Error!),
        };
    }

    private static async Task<IResult> SendAsync(
        Guid id, SendMessageRequest request, HttpContext http,
        ChatService chatService, CancellationToken ct)
    {
        var (userId, session) = AuthEndpoints.Current(http);
        _ = session;
        var role = http.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "";
        var outcome = await chatService.SendAsync(new ChatService.SendInput(
            id, userId, role, request.Body ?? "", request.ReplyToMessageId,
            request.MentionedUserIds ?? [], request.MentionEveryone,
            request.AttachmentBlobIds ?? []), ct);
        return outcome.Failure switch
        {
            ChatService.Failure.None => Results.Created(
                $"/api/v1/messages/{outcome.Value!.Id}", new { messageId = outcome.Value.Id }),
            ChatService.Failure.Forbidden => Problems.Forbidden(http, outcome.Error!),
            ChatService.Failure.NotFound => Problems.NotFound(http, outcome.Error!),
            _ => Problems.Validation(http, outcome.Error!),
        };
    }

    private static async Task<IResult> EditAsync(
        Guid id, EditMessageRequest request, HttpContext http,
        ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var outcome = await chatService.EditAsync(id, userId, request.Body ?? "", ct);
        return outcome.Failure switch
        {
            ChatService.Failure.None => Results.NoContent(),
            ChatService.Failure.Forbidden => Problems.Forbidden(http, outcome.Error!),
            ChatService.Failure.NotFound => Problems.NotFound(http, outcome.Error!),
            _ => Problems.Validation(http, outcome.Error!),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, HttpContext http, ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var outcome = await chatService.DeleteAsync(id, userId, ct);
        return outcome.Failure switch
        {
            ChatService.Failure.None => Results.NoContent(),
            ChatService.Failure.Forbidden => Problems.Forbidden(http, outcome.Error!),
            _ => Problems.NotFound(http, outcome.Error!),
        };
    }

    private static async Task<IResult> ReactAsync(
        Guid id, string reaction, HttpContext http, ChatService chatService,
        bool add, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await chatService.ReactAsync(id, userId, reaction, add, ct) switch
        {
            ChatService.Failure.None => Results.NoContent(),
            ChatService.Failure.Forbidden => Problems.Forbidden(http, "Not your conversation."),
            ChatService.Failure.Validation => Problems.Validation(http, "Invalid reaction."),
            _ => Problems.NotFound(http, "Message not found."),
        };
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id, MarkReadRequest request, HttpContext http,
        ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await chatService.MarkReadAsync(id, userId, request.MessageId, ct) switch
        {
            ChatService.Failure.None => Results.NoContent(),
            _ => Problems.NotFound(http, "Conversation or message not found."),
        };
    }

    private static async Task<IResult> MuteAsync(
        Guid id, HttpContext http, ChatService chatService, bool mute, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await chatService.SetMutedAsync(userId, id, mute, ct) switch
        {
            ChatService.Failure.None => Results.NoContent(),
            ChatService.Failure.Forbidden => Problems.Forbidden(
                http, "Mandatory groups cannot be muted.", "mandatoryGroup"),
            _ => Problems.NotFound(http, "Conversation not found."),
        };
    }

    private static async Task<IResult> UploadAttachmentAsync(
        Guid id, HttpContext http, IAppDb db, IFileBlobStore blobs,
        ChatService chatService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var members = await chatService.MemberIdsAsync(id, ct);
        if (!members.Contains(userId))
        {
            return Problems.Forbidden(http, "You are not in this conversation.");
        }

        if (!http.Request.HasFormContentType)
        {
            return Problems.Validation(http, "Send the attachment as multipart form data.");
        }

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Problems.Validation(http, "A 'file' upload is required.");
        }

        if (file.Length > MaxAttachmentBytes)
        {
            return Problems.Validation(http, "Attachments are limited to 25 MB.");
        }

        if (!AttachmentContentTypes.Contains(file.ContentType))
        {
            return Problems.Validation(http, "That file type is not allowed in chat.");
        }

        await using var stream = file.OpenReadStream();
        var blob = await blobs.SaveAsync(stream, file.FileName, file.ContentType, userId, ct);
        return Results.Ok(new { blobId = blob.Id });
    }

    private static async Task<MessageDto> ToDtoAsync(
        Message message, IAppDb db,
        IReadOnlyDictionary<Guid, string> users, CancellationToken ct)
    {
        var attachments = await db.MessageAttachments
            .Where(a => a.MessageId == message.Id)
            .Select(a => new AttachmentDto(a.BlobId, a.OriginalName, a.ContentType, a.ByteLength))
            .ToListAsync(ct);
        var reactions = await db.MessageReactions
            .Where(r => r.MessageId == message.Id)
            .GroupBy(r => r.Reaction)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        return new MessageDto(
            message.Id, message.ConversationId, message.SenderUserId,
            users.GetValueOrDefault(message.SenderUserId, "Unknown"),
            message.Body, message.CreatedAtUtc, message.EditedAtUtc,
            message.DeletedAtUtc is not null, message.ReplyToMessageId,
            attachments, reactions);
    }
}
