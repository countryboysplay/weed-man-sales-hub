using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SalesHub.Api.Auth.Requirements;
using SalesHub.Application;
using SalesHub.Domain;

namespace SalesHub.Api.Auth
{

public static class AuthorizationSetup
{
    public static IServiceCollection AddSalesHubAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, FreshAuthHandler>();
        services.AddSingleton<IAuthorizationHandler, MonitoredWorkSessionHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SalesHubAuthorizationResultHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Employee, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole([.. Roles.All]))
            .AddPolicy(Policies.Management, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole([.. Roles.Management]))
            .AddPolicy(Policies.SupervisorOrAbove, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole([.. Roles.Management]))
            .AddPolicy(Policies.ManagerOrOwner, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole([.. Roles.ManagerOrOwner]))
            .AddPolicy(Policies.OwnerOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Roles.Owner))
            .AddPolicy(Policies.FreshAuthRequired, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new FreshAuthRequirement()))
            .AddPolicy(Policies.MonitoredWorkSession, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole([.. Roles.All])
                .AddRequirements(new MonitoredWorkSessionRequirement()));

        return services;
    }
}

}

namespace SalesHub.Api.Auth.Requirements
{
    using Microsoft.AspNetCore.Authorization.Policy;
    using SalesHub.Domain.Entities;

    public sealed class FreshAuthRequirement : IAuthorizationRequirement;

    /// <summary>
    /// Fresh authentication (CLAUDE.md §3): the server-held assertion on the
    /// session row must still be inside the window. Never a client token.
    /// </summary>
    public sealed class FreshAuthHandler(BusinessTime businessTime)
        : AuthorizationHandler<FreshAuthRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context, FreshAuthRequirement requirement)
        {
            if (context.Resource is HttpContext http
                && http.Items[AuthConstants.SessionItemKey] is UserSession session
                && session.FreshAuthUntilUtc is { } until
                && until > businessTime.UtcNow)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    public sealed class MonitoredWorkSessionRequirement : IAuthorizationRequirement;

    /// <summary>
    /// The mandatory Idle Detection gate (CLAUDE.md §4): for monitored roles,
    /// working endpoints demand a Verified capability with a live lease.
    /// Non-monitored roles pass — the requirement targets presence-monitored
    /// work sessions, not management consoles.
    /// </summary>
    public sealed class MonitoredWorkSessionHandler(
        IOptions<SecurityOptions> securityOptions,
        BusinessTime businessTime)
        : AuthorizationHandler<MonitoredWorkSessionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context, MonitoredWorkSessionRequirement requirement)
        {
            if (context.Resource is not HttpContext http
                || http.Items[AuthConstants.SessionItemKey] is not UserSession session)
            {
                return Task.CompletedTask;
            }

            var role = http.Items[AuthConstants.UserRoleItemKey] as string ?? string.Empty;
            var monitored = securityOptions.Value.MonitoredRoles
                .Contains(role, StringComparer.Ordinal);

            if (!monitored)
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            var leaseAlive = session.IdleCapabilityLeaseUntilUtc is { } lease
                && lease > businessTime.UtcNow;

            if (session.IdleCapabilityState.PermitsMonitoredWork() && leaseAlive)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Maps authorization failures to the standardized ProblemDetails codes:
    /// a missing idle capability is 403 "idleCapabilityRequired" and missing
    /// fresh auth is 403 "requiredFreshAuth", so the client can route to the
    /// right remediation screen (docs/02 extensions).
    /// </summary>
    public sealed class SalesHubAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _fallback = new();

        public async Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure is { } failure)
            {
                if (failure.FailedRequirements.OfType<MonitoredWorkSessionRequirement>().Any())
                {
                    await WriteProblemAsync(context,
                        "idleCapabilityRequired",
                        "Idle Detection capability is required.",
                        "This work session requires a verified Idle Detection capability. " +
                        "Enable the permission and verify again.");
                    return;
                }

                if (failure.FailedRequirements.OfType<FreshAuthRequirement>().Any())
                {
                    await WriteProblemAsync(context,
                        "requiredFreshAuth",
                        "Fresh authentication required.",
                        "Re-enter your password to continue with this sensitive action.");
                    return;
                }
            }

            await _fallback.HandleAsync(next, context, policy, authorizeResult);
        }

        private static Task WriteProblemAsync(
            HttpContext context, string code, string title, string detail)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new
            {
                type = $"https://saleshub/problems/{code}",
                title,
                status = 403,
                detail,
                code,
                correlationId = context.TraceIdentifier,
            });
        }
    }
}
