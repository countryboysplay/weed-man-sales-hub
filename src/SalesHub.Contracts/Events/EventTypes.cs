namespace SalesHub.Contracts.Events;

/// <summary>Event-type names follow docs/12: module.entityAction.v1.</summary>
public static class EventTypes
{
    public const string SessionRevoked = "auth.sessionRevoked.v1";
    public const string UserCreated = "users.userCreated.v1";
    public const string UserDeactivated = "users.userDeactivated.v1";
    public const string UserReactivated = "users.userReactivated.v1";
    public const string NotificationCreated = "notifications.notificationCreated.v1";
    public const string PasswordResetRequested = "auth.passwordResetRequested.v1";
    public const string SystemPing = "system.ping.v1";
}
