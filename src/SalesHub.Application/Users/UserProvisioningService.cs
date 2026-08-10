using SalesHub.Application.Abstractions;
using SalesHub.Contracts.Events;
using SalesHub.Domain;

namespace SalesHub.Application.Users;

/// <summary>
/// Management-only user creation (Wave 0 gate: "user can be created by
/// seed/management dev endpoint"). There is no public self-registration.
/// Owner accounts cannot be created here — CLAUDE.md §19 requires the
/// protected Owner workflow, which arrives in Wave 6; until then Owners come
/// only from seeding.
/// </summary>
public sealed class UserProvisioningService(
    IAppDb db,
    IIdentityService identity,
    IAuditWriter audit,
    IOutboxWriter outbox)
{
    public sealed record CreateUserInput(
        string Username,
        string TemporaryPassword,
        string DisplayName,
        string Role,
        string? Email,
        Guid ActorUserId,
        Guid? ActorSessionId);

    public sealed record CreateUserOutcome(bool Created, string? Error, AppUserInfo? User);

    public async Task<CreateUserOutcome> CreateAsync(CreateUserInput input, CancellationToken ct = default)
    {
        if (!Roles.IsValid(input.Role))
        {
            return new CreateUserOutcome(false, $"Unknown role '{input.Role}'.", null);
        }

        if (input.Role == Roles.Owner)
        {
            // The user-admin mockup's plain dropdown lists Owner, but the
            // written rule wins: Owner creation demands the protected flow.
            return new CreateUserOutcome(
                false,
                "Owner accounts require the protected Owner workflow, not ordinary user creation.",
                null);
        }

        AppUserInfo user;
        try
        {
            user = await identity.CreateUserAsync(new NewUser(
                input.Username, input.TemporaryPassword, input.DisplayName, input.Role, input.Email), ct);
        }
        catch (IdentityOperationException ex)
        {
            return new CreateUserOutcome(false, ex.Message, null);
        }

        await db.ExecuteInTransactionAsync(async token =>
        {
            await audit.WriteAsync(new AuditEntry("users", "users.created", AuditRetentionClass.AccountLifetime)
            {
                ActorUserId = input.ActorUserId,
                TargetType = "User",
                TargetId = user.Id.ToString(),
                SessionId = input.ActorSessionId,
                After = new { user.Username, user.DisplayName, user.Role },
            }, token);

            await outbox.EnqueueAsync(EventTypes.UserCreated, new
            {
                userId = user.Id,
                displayName = user.DisplayName,
                role = user.Role,
            }, token);

            await db.SaveChangesAsync(token);
        }, ct);

        return new CreateUserOutcome(true, null, user);
    }
}

/// <summary>Thrown by IIdentityService implementations for expected failures
/// (duplicate username, weak password) with a safe, user-facing message.</summary>
public sealed class IdentityOperationException(string message) : Exception(message);
