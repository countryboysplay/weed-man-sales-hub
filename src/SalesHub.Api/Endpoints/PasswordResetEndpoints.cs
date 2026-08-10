using SalesHub.Api.Auth;
using SalesHub.Application.Abstractions;
using SalesHub.Application.Users;
using SalesHub.Contracts.Users;

namespace SalesHub.Api.Endpoints;

/// <summary>Management queue for the mediated forgot-password flow.</summary>
public static class PasswordResetEndpoints
{
    public static IEndpointRouteBuilder MapPasswordResetEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/password-reset-requests")
            .RequireAuthorization(Policies.Management);
        group.MapGet("/", ListAsync);
        group.MapPost("/{id:guid}/complete", CompleteAsync);
        group.MapPost("/{id:guid}/dismiss", DismissAsync);
        return api;
    }

    private static async Task<IResult> ListAsync(
        PasswordResetService service, IIdentityService identity, CancellationToken ct)
    {
        var open = await service.ListOpenAsync(ct);
        var result = new List<PasswordResetRequestDto>(open.Count);
        foreach (var request in open)
        {
            string? matchedName = null;
            if (request.MatchedUserId is { } matchedId)
            {
                matchedName = (await identity.FindByIdAsync(matchedId, ct))?.DisplayName;
            }

            result.Add(new PasswordResetRequestDto(
                request.Id, request.UsernameSubmitted, matchedName, request.MatchedUserId,
                request.Status.ToString(), request.CreatedAtUtc, request.HandledAtUtc));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CompleteAsync(
        Guid id, CompletePasswordResetRequest request, HttpContext http,
        PasswordResetService service, CancellationToken ct)
    {
        var (outcome, error) = await service.CompleteAsync(
            id, UserLifecycleEndpoints.ActorOf(http), request.NewPassword, ct);
        return outcome switch
        {
            PasswordResetService.CompleteOutcome.Done => Results.NoContent(),
            PasswordResetService.CompleteOutcome.NotFound =>
                Problems.NotFound(http, "No open request with that id."),
            PasswordResetService.CompleteOutcome.Forbidden =>
                Problems.Forbidden(http, error ?? "Not allowed."),
            _ => Problems.Validation(http, error ?? "Invalid request."),
        };
    }

    private static async Task<IResult> DismissAsync(
        Guid id, HttpContext http, PasswordResetService service, CancellationToken ct) =>
        await service.DismissAsync(id, UserLifecycleEndpoints.ActorOf(http), ct)
            ? Results.NoContent()
            : Problems.NotFound(http, "No open request with that id.");
}
