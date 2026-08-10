using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SalesHub.Domain;

namespace SalesHub.Api.Hubs;

/// <summary>
/// The authenticated application hub (/hubs/app, docs/03): sales totals,
/// announcements, tasks, approvals, notifications, system commands. Group
/// membership is routing only — data authorization stays in policies and
/// resource checks server-side.
/// </summary>
[Authorize]
public sealed class AppHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);

        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        }

        // Whole-company broadcasts: team sales totals and celebrations reach
        // every signed-in user (docs/03, sales-celebrations mockup).
        await Groups.AddToGroupAsync(Context.ConnectionId, "all");

        if (role is not null && Roles.IsValid(role))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"role:{role}");
            if (Roles.IsManagement(role))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "management");
            }

            if (role == Roles.Owner)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "owners");
            }
        }

        await base.OnConnectedAsync();
    }
}
