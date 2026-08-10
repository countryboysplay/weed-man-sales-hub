namespace SalesHub.Contracts.Users;

public sealed record CreateUserRequest(
    string Username,
    string TemporaryPassword,
    string DisplayName,
    string Role,
    string? Email);

public sealed record UserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive);
