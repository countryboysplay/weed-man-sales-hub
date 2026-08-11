namespace SalesHub.Contracts.Work;

public sealed record CreateTaskRequest(
    string Title,
    string Description = "",
    string Priority = "Normal",           // Normal | High
    DateTimeOffset? DueAt = null,
    string Recurrence = "None",           // None | Daily | Weekly | Monthly
    bool OverdueReminders = true,
    bool AssignToEveryone = false,
    IReadOnlyList<Guid>? AssigneeUserIds = null);

public sealed record TaskInstanceDto(
    Guid InstanceId,
    Guid DefinitionId,
    string Title,
    string Description,
    string Priority,
    string Recurrence,
    DateTimeOffset? DueAt,
    string Status,
    DateTimeOffset? CompletedAt,
    Guid AssigneeUserId,
    string AssigneeDisplayName,
    int CommentCount);

public sealed record TaskCommentRequest(
    string Body,
    IReadOnlyList<Guid>? MentionedUserIds = null);

public sealed record TaskProgressRow(
    Guid InstanceId,
    Guid AssigneeUserId,
    string AssigneeDisplayName,
    string Status,
    DateTimeOffset? CompletedAt);

public sealed record TaskProgressResponse(
    Guid DefinitionId,
    string Title,
    int Assigned,
    int Completed,
    int Percent,
    IReadOnlyList<TaskProgressRow> Rows);

public sealed record IssueRecognitionRequest(
    Guid RecipientUserId,
    Guid BadgeId,
    string Category = "",
    string Message = "");

public sealed record RecognitionDto(
    Guid Id,
    Guid RecipientUserId,
    string RecipientDisplayName,
    string AuthorDisplayName,
    string BadgeName,
    string BadgeEmoji,
    string Category,
    string Message,
    DateTimeOffset CreatedAt,
    bool Archived,
    IReadOnlyDictionary<string, int> Reactions,
    int CommentCount);

public sealed record BadgeDto(Guid Id, string Name, string Emoji, bool BuiltIn, bool Active);

public sealed record CreateBadgeRequest(string Name, string Emoji);

public sealed record RecognitionCommentRequest(string Body);
