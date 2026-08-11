using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Hubs;

/// <summary>
/// Chat-specific realtime (/hubs/chat, docs/03). Typing indicators are
/// ephemeral: broadcast to current members, throttled client-side, never
/// stored. Durable chat events (messages, receipts) ride the outbox through
/// the app hub instead — SignalR is never the only copy of anything.
/// </summary>
[Authorize]
public sealed class ChatHub(SalesHubDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var conversationIds = await db.ConversationMembers
                .Where(m => m.UserId == userId)
                .Select(m => m.ConversationId)
                .ToListAsync(Context.ConnectionAborted);
            foreach (var conversationId in conversationIds)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation:{conversationId}");
            }
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Typing start/stop — membership is checked server-side.</summary>
    public async Task Typing(Guid conversationId, bool isTyping)
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }

        var isMember = await db.ConversationMembers.AnyAsync(
            m => m.ConversationId == conversationId && m.UserId == userId);
        if (!isMember)
        {
            return; // not an error channel — just drop it
        }

        await Clients.OthersInGroup($"conversation:{conversationId}")
            .SendAsync("typing", new { conversationId, userId, isTyping });
    }
}
