using Microsoft.EntityFrameworkCore;
using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Recognitions;
using SalesHub.Contracts.Work;
using SalesHub.Domain;
using SalesHub.Domain.Entities;

namespace SalesHub.Api.Endpoints;

public static class RecognitionEndpoints
{
    public static IEndpointRouteBuilder MapRecognitionEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/recognitions").RequireAuthorization(Policies.Employee);
        group.MapGet("/", FeedAsync);
        group.MapPost("/", IssueAsync).RequireAuthorization(Policies.Management);
        group.MapPost("/{id:guid}/reactions", ReactAsync);
        group.MapPost("/{id:guid}/comments", CommentAsync);

        var badges = api.MapGroup("/recognition-badges").RequireAuthorization(Policies.Employee);
        badges.MapGet("/", BadgesAsync);
        badges.MapPost("/", CreateBadgeAsync).RequireAuthorization(Policies.Management);

        return api;
    }

    private static async Task<IResult> FeedAsync(
        IAppDb db, IIdentityService identity, bool archived, CancellationToken ct)
    {
        var users = (await identity.ListUsersAsync(new UserQuery(IncludeInactive: true), ct))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        var rows = await db.Recognitions
            .Where(r => archived ? r.ArchivedAtUtc != null : r.ArchivedAtUtc == null)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(100)
            .Join(db.RecognitionBadges, r => r.BadgeId, b => b.Id, (r, b) => new { r, b })
            .ToListAsync(ct);

        var result = new List<RecognitionDto>(rows.Count);
        foreach (var row in rows)
        {
            var reactions = await db.RecognitionReactions
                .Where(x => x.RecognitionId == row.r.Id)
                .GroupBy(x => x.Reaction)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var comments = await db.RecognitionComments
                .CountAsync(c => c.RecognitionId == row.r.Id, ct);
            result.Add(new RecognitionDto(
                row.r.Id, row.r.RecipientUserId,
                users.GetValueOrDefault(row.r.RecipientUserId, "Unknown"),
                users.GetValueOrDefault(row.r.AuthorUserId, "Unknown"),
                row.b.Name, row.b.Emoji, row.r.Category, row.r.Message,
                row.r.CreatedAtUtc, row.r.ArchivedAtUtc is not null,
                reactions, comments));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> IssueAsync(
        IssueRecognitionRequest request, HttpContext http,
        RecognitionService service, CancellationToken ct)
    {
        var (userId, _) = AuthEndpoints.Current(http);
        var (recognition, error) = await service.PublishAsync(
            userId, request.RecipientUserId, request.BadgeId,
            request.Category, request.Message, ct);
        return recognition is null
            ? Problems.Validation(http, error!)
            : Results.Created($"/api/v1/recognitions/{recognition.Id}", new { recognition.Id });
    }

    private static async Task<IResult> ReactAsync(
        Guid id, ReactionBody body, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Reaction) || body.Reaction.Length > 32)
        {
            return Problems.Validation(http, "Invalid reaction.");
        }

        var exists = await db.Recognitions.AnyAsync(r => r.Id == id, ct);
        if (!exists)
        {
            return Problems.NotFound(http, "Recognition not found.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        var already = await db.RecognitionReactions.AnyAsync(
            r => r.RecognitionId == id && r.UserId == userId && r.Reaction == body.Reaction, ct);
        if (!already)
        {
            db.RecognitionReactions.Add(new RecognitionReaction
            {
                RecognitionId = id,
                UserId = userId,
                Reaction = body.Reaction,
                CreatedAtUtc = businessTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CommentAsync(
        Guid id, RecognitionCommentRequest request, HttpContext http, IAppDb db,
        BusinessTime businessTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Problems.Validation(http, "A comment needs text.");
        }

        var exists = await db.Recognitions.AnyAsync(r => r.Id == id, ct);
        if (!exists)
        {
            return Problems.NotFound(http, "Recognition not found.");
        }

        var (userId, _) = AuthEndpoints.Current(http);
        db.RecognitionComments.Add(new RecognitionComment
        {
            Id = Guid.CreateVersion7(),
            RecognitionId = id,
            AuthorUserId = userId,
            Body = request.Body.Trim(),
            CreatedAtUtc = businessTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> BadgesAsync(IAppDb db, CancellationToken ct) =>
        Results.Ok(await db.RecognitionBadges
            .Where(b => b.Active)
            .OrderByDescending(b => b.BuiltIn).ThenBy(b => b.Name)
            .Select(b => new BadgeDto(b.Id, b.Name, b.Emoji, b.BuiltIn, b.Active))
            .ToListAsync(ct));

    private static async Task<IResult> CreateBadgeAsync(
        CreateBadgeRequest request, HttpContext http, IAppDb db, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 64)
        {
            return Problems.Validation(http, "A badge needs a name up to 64 characters.");
        }

        if (await db.RecognitionBadges.AnyAsync(b => b.Name == name, ct))
        {
            return Problems.Conflict(http, $"Badge '{name}' already exists.");
        }

        var badge = new RecognitionBadge
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Emoji = request.Emoji?.Trim() ?? "",
            BuiltIn = false,
        };
        db.RecognitionBadges.Add(badge);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/recognition-badges/{badge.Id}",
            new BadgeDto(badge.Id, badge.Name, badge.Emoji, false, true));
    }

    private sealed record ReactionBody(string Reaction);
}
