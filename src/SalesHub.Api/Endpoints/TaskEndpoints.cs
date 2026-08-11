using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Tasks;
using SalesHub.Contracts.Work;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder api)
    {
        var tasks = api.MapGroup("/tasks").RequireAuthorization(Policies.Employee);
        tasks.MapGet("/my", MyTasksAsync);
        tasks.MapPost("/{id:guid}/complete", CompleteAsync);
        tasks.MapPost("/{id:guid}/comments", CommentAsync);
        tasks.MapPost("/", CreateAsync).RequireAuthorization(Policies.Management);
        tasks.MapGet("/definitions/{id:guid}/progress", ProgressAsync)
            .RequireAuthorization(Policies.Management);
        return api;
    }

    private static async Task<IResult> MyTasksAsync(
        HttpContext http, IAppDb db, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var rows = await db.TaskInstances
            .Where(t => t.AssigneeUserId == userId && t.Status == WorkTaskStatus.Active)
            .Join(db.TaskDefinitions, t => t.DefinitionId, d => d.Id, (t, d) => new { t, d })
            .OrderBy(x => x.t.DueAtUtc == null)
            .ThenBy(x => x.t.DueAtUtc)
            .Select(x => new
            {
                x.t, x.d,
                CommentCount = db.TaskComments.Count(c => c.InstanceId == x.t.Id),
            })
            .ToListAsync(ct);

        return Results.Ok(rows.Select(x => new TaskInstanceDto(
            x.t.Id, x.d.Id, x.d.Title, x.d.Description, x.d.Priority.ToString(),
            x.d.Recurrence.ToString(), x.t.DueAtUtc ?? x.d.DueAtUtc,
            x.t.Status.ToString(), x.t.CompletedAtUtc,
            x.t.AssigneeUserId, "", x.CommentCount)).ToList());
    }

    private static async Task<IResult> CreateAsync(
        CreateTaskRequest request, HttpContext http, TaskService taskService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        if (!Enum.TryParse<TaskPriority>(request.Priority, true, out var priority)
            || !Enum.TryParse<TaskRecurrence>(request.Recurrence, true, out var recurrence))
        {
            return Problems.Validation(http, "Unknown priority or recurrence.");
        }

        var (definition, error) = await taskService.CreateAsync(new TaskService.CreateInput(
            userId, request.Title, request.Description, priority, request.DueAt,
            recurrence, request.OverdueReminders, request.AssignToEveryone,
            request.AssigneeUserIds ?? []), ct);
        return definition is null
            ? Problems.Validation(http, error!)
            : Results.Created($"/api/v1/tasks/definitions/{definition.Id}", new { definition.Id });
    }

    private static async Task<IResult> CompleteAsync(
        Guid id, HttpContext http, TaskService taskService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        return await taskService.CompleteAsync(id, userId, ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "No active task of yours with that id.");
    }

    private static async Task<IResult> CommentAsync(
        Guid id, TaskCommentRequest request, HttpContext http,
        TaskService taskService, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var role = http.User.FindFirstValue(ClaimTypes.Role) ?? "";
        var (ok, error) = await taskService.CommentAsync(
            id, userId, Roles.IsManagement(role), request.Body,
            request.MentionedUserIds ?? [], ct);
        return ok ? Results.NoContent() : Problems.NotFound(http, error!);
    }

    private static async Task<IResult> ProgressAsync(
        Guid id, HttpContext http, IAppDb db, IIdentityService identity, CancellationToken ct)
    {
        var definition = await db.TaskDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (definition is null)
        {
            return Problems.NotFound(http, "Task not found.");
        }

        var instances = await db.TaskInstances
            .Where(t => t.DefinitionId == id)
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var completed = instances.Count(t => t.Status == WorkTaskStatus.Completed);
        return Results.Ok(new TaskProgressResponse(
            id, definition.Title, instances.Count, completed,
            instances.Count == 0 ? 0 : (int)Math.Round(completed * 100.0 / instances.Count),
            instances.Select(t => new TaskProgressRow(
                t.Id, t.AssigneeUserId,
                users.GetValueOrDefault(t.AssigneeUserId, "Unknown"),
                t.Status.ToString(), t.CompletedAtUtc)).ToList()));
    }
}
