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
    public const string SaleCreated = "sales.saleCreated.v1";
    public const string SaleUpdated = "sales.saleUpdated.v1";
    public const string SaleDeleted = "sales.saleDeleted.v1";
    public const string SaleCorrected = "sales.saleCorrected.v1";
    public const string MessageCreated = "chat.messageCreated.v1";
    public const string MessageEdited = "chat.messageEdited.v1";
    public const string MessageDeleted = "chat.messageDeleted.v1";
    public const string ReactionChanged = "chat.reactionChanged.v1";
    public const string ReadPositionChanged = "chat.readPositionChanged.v1";
    public const string ConversationChanged = "chat.conversationChanged.v1";
    public const string AnnouncementPublished = "announcements.published.v1";
    public const string AnnouncementProgressChanged = "announcements.progressChanged.v1";
    public const string TaskAssigned = "tasks.taskAssigned.v1";
    public const string TaskCompleted = "tasks.taskCompleted.v1";
    public const string RecognitionPublished = "recognitions.published.v1";
    public const string PresenceStatusChanged = "presence.statusChanged.v1";
    public const string PresenceFlagRaised = "presence.flagRaised.v1";
    public const string TimeOffDecided = "timeoff.decided.v1";
    public const string ApprovalsChanged = "approvals.queueChanged.v1";
    public const string RemoteDeviceCommandIssued = "sync.remoteCommandIssued.v1";
    public const string SystemPing = "system.ping.v1";
}
