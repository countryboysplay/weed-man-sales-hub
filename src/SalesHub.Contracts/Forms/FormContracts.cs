namespace SalesHub.Contracts.Forms;

/// <summary>The native builder graph (CLAUDE.md §10), stored per version.</summary>
public sealed record FormDefinition(IReadOnlyList<FormSection> Sections);

public sealed record FormSection(
    string Id,
    string Title,
    IReadOnlyList<FormField> Fields);

public sealed record FormField(
    string Id,
    string Label,
    string Type,                       // SingleLine | Number | Dropdown | YesNo
    bool Required = false,
    IReadOnlyList<string>? Options = null,
    FieldCondition? VisibleWhen = null);

/// <summary>Conditional branching: show this field only when another field's
/// answer equals a value.</summary>
public sealed record FieldCondition(string FieldId, string Value);

public sealed record CreateNativeFormRequest(
    string DisplayName,
    FormDefinition Definition,
    bool TracksCompletion = false,
    bool Publish = false);

public sealed record UpdateNativeFormRequest(
    string? DisplayName,
    FormDefinition? Definition,
    bool? Publish);

public sealed record CreateGoogleLinkRequest(string DisplayName, string ExternalUrl);

public sealed record FormListItem(
    Guid Id,
    string Type,
    string DisplayName,
    string Status,
    string? ExternalUrl,
    bool TracksCompletion,
    int VersionNumber);

public sealed record FormDetailResponse(
    Guid Id,
    string Type,
    string DisplayName,
    string Status,
    string? ExternalUrl,
    Guid? VersionId,
    FormDefinition? Definition);

public sealed record SubmitFormRequest(IReadOnlyDictionary<string, string> Answers);

public sealed record SubmissionDto(
    Guid Id,
    Guid FormId,
    string FormDisplayName,
    Guid UserId,
    string UserDisplayName,
    string Status,
    DateTimeOffset SubmittedAt,
    IReadOnlyDictionary<string, string> Answers);

public sealed record CreateEmailRequestRequest(
    string Cid,
    string CustomerEmail,
    string QuoteType,
    string LawnArea,
    string Coverage = "");

public sealed record EmailRequestDto(
    Guid Id,
    Guid SubmitterUserId,
    string SubmitterDisplayName,
    string Cid,
    string CustomerEmail,
    string QuoteType,
    string LawnArea,
    string Coverage,
    DateTimeOffset CreatedAt);
