namespace SalesHub.Contracts.Records;

public sealed record CreateNoteRequest(
    string Category,
    string Priority,           // Normal | High
    string Body,
    bool RequireAcknowledgment = false,
    IReadOnlyList<Guid>? AckTargetUserIds = null);

public sealed record FollowupRequest(string Body);

public sealed record ResolveNoteRequest(string ResolutionNote);

public sealed record ReopenNoteRequest(string Reason);

public sealed record LinkRecordRequest(string TargetPublicId);

public sealed record UnlinkRecordRequest(string Reason);

public sealed record CreateTagRequest(string Label);

public sealed record TagEntityRequest(string EntityPublicId);

public sealed record NoteFollowupDto(
    Guid Id,
    Guid AuthorUserId,
    string AuthorName,
    string Kind,
    string Body,
    DateTimeOffset CreatedAtUtc);

public sealed record NoteAckTargetDto(
    Guid TargetUserId,
    string TargetName,
    DateTimeOffset? AcknowledgedAtUtc);

public sealed record RecordLinkDto(
    Guid Id,
    string TargetPublicId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RemovedAtUtc,
    string? RemoveReason);

public sealed record ManagementNoteDto(
    Guid Id,
    string PublicId,
    Guid EmployeeUserId,
    string Category,
    string Priority,
    string Status,
    string Body,
    Guid CreatedByUserId,
    string CreatedByName,
    DateTimeOffset CreatedAtUtc,
    int? PinnedRank,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAtUtc,
    IReadOnlyList<NoteFollowupDto> Followups,
    IReadOnlyList<NoteAckTargetDto> AckTargets,
    IReadOnlyList<RecordLinkDto> Links,
    IReadOnlyList<string> Tags);

public sealed record RelatedRecordDto(
    string PublicId,
    string Kind,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record EmployeeManagementRecordDto(
    Guid EmployeeUserId,
    string DisplayName,
    string Role,
    bool IsActive,
    IReadOnlyList<ManagementNoteDto> Notes,
    IReadOnlyList<RelatedRecordDto> RelatedRecords);

public sealed record ManagementTagDto(Guid Id, string Label, bool Active);
