namespace SalesHub.Contracts.Users;

public sealed record UserDetailsResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive,
    string? Email,
    string? Phone,
    Guid? BranchId,
    string? BranchName,
    DateOnly? HireDate,
    DateOnly? Birthday,
    bool HasProfilePhoto,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeactivatedAt,
    string? DeactivationReason,
    DateTimeOffset? ScheduledReactivationAt,
    int ActiveSessions);

public sealed record UpdateUserRequest(
    string? DisplayName,
    string? Role,
    string? Email,
    Guid? BranchId,
    DateOnly? HireDate);

public sealed record DeactivateUserRequest(
    string? Reason,
    DateTimeOffset? ScheduledReactivationAt);

public sealed record ScheduleReactivationRequest(DateTimeOffset ReactivateAt);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record DirectoryEntry(
    Guid Id,
    string DisplayName,
    string Role,
    string? BranchName,
    string? Email,
    string? Phone,
    bool HasProfilePhoto);

public sealed record BranchDto(Guid Id, string Name, bool Active);

public sealed record CreateBranchRequest(string Name);

public sealed record ProfileResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    string? BranchName,
    string? Email,
    string? Phone,
    DateOnly? HireDate,
    DateOnly? Birthday,
    bool HasProfilePhoto);

public sealed record UpdateProfileRequest(string? Phone, DateOnly? Birthday);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record PasswordResetRequestDto(
    Guid Id,
    string UsernameSubmitted,
    string? MatchedDisplayName,
    Guid? MatchedUserId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HandledAt);

public sealed record CompletePasswordResetRequest(string NewPassword);
