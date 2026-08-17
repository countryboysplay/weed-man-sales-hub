namespace SalesHub.Contracts.Support;

public sealed record CreateSupportTicketRequest(
    string IssueType,
    string Description,
    string? Page,
    string? AppVersion,
    Guid? AttachmentBlobId);

public sealed record SupportReplyRequest(string Body, string? Visibility);

public sealed record AssignTicketRequest(Guid PrimaryAssigneeUserId);

public sealed record AddCollaboratorRequest(Guid UserId);

public sealed record SetTicketPriorityRequest(string Priority);

public sealed record LinkTicketRequest(string TargetPublicId);

public sealed record SupportMessageDto(
    Guid Id,
    Guid AuthorUserId,
    string AuthorName,
    string Visibility,
    string Body,
    DateTimeOffset CreatedAtUtc);

/// <summary>Manager/Owner only — supervisors and reporters never receive it
/// (permission matrix: advanced support diagnostics).</summary>
public sealed record SupportDiagnosticsDto(
    string AppVersion,
    string BrowserFamily,
    string DeviceId,
    string CorrelationId);

public sealed record SupportTicketDto(
    Guid Id,
    string PublicId,
    Guid ReporterUserId,
    string ReporterName,
    string IssueType,
    string Description,
    string Page,
    string Priority,
    string? SuggestedPriority,
    string? SuggestedPriorityReason,
    string Status,
    Guid? PrimaryAssigneeUserId,
    string? PrimaryAssigneeName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    bool ForceClosed,
    bool ReporterConfirmedClosure,
    IReadOnlyList<SupportMessageDto> Messages,
    IReadOnlyList<Guid> CollaboratorUserIds,
    IReadOnlyList<string> LinkedPublicIds,
    SupportDiagnosticsDto? Diagnostics);

public sealed record SupportTicketSummaryDto(
    Guid Id,
    string PublicId,
    Guid ReporterUserId,
    string ReporterName,
    string IssueType,
    string Priority,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateTicketResponse(
    Guid Id,
    string PublicId,
    string SuggestedPriority,
    IReadOnlyList<SupportTicketSummaryDto> SimilarTickets);
