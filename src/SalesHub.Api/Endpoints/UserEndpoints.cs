using System.Security.Claims;
using SalesHub.Api.Auth;
using SalesHub.Application.Users;
using SalesHub.Contracts.Users;

namespace SalesHub.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder api)
    {
        // Wave 0 gate: "user can be created by seed/management dev endpoint".
        // Full lifecycle (deactivate/reactivate/reset/force-logout) is Wave 1.
        api.MapPost("/users", CreateUserAsync)
            .RequireAuthorization(Policies.Management);

        return api;
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        HttpContext http,
        UserProvisioningService provisioning,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.TemporaryPassword)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.Role))
        {
            return Problems.Validation(http,
                "Username, temporary password, display name and role are required.");
        }

        var (userId, session) = AuthEndpoints.Current(http);
        var outcome = await provisioning.CreateAsync(new UserProvisioningService.CreateUserInput(
            request.Username.Trim(),
            request.TemporaryPassword,
            request.DisplayName.Trim(),
            request.Role.Trim(),
            request.Email?.Trim(),
            userId,
            session.Id), ct);

        if (!outcome.Created)
        {
            return outcome.Error!.Contains("protected Owner workflow", StringComparison.Ordinal)
                ? Problems.Forbidden(http, outcome.Error, "protectedOwnerWorkflowRequired")
                : Problems.Validation(http, outcome.Error!);
        }

        var user = outcome.User!;
        return Results.Created(
            $"/api/v1/users/{user.Id}",
            new UserResponse(user.Id, user.Username, user.DisplayName, user.Role, user.IsActive));
    }
}
