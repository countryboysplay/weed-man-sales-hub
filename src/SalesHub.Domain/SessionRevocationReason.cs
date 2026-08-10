namespace SalesHub.Domain;

/// <summary>
/// Why a server-side session ended. The client renders the "Session ended"
/// screen from this, so the values match the approved login-states mockup:
/// "password reset, account security action, or administrative logout".
/// </summary>
public enum SessionRevocationReason
{
    UserLogout = 0,
    UserSignedOutOtherDevice = 1,
    PasswordReset = 2,
    SecurityAction = 3,
    AdministrativeLogout = 4,
    AccountDeactivated = 5,
    Expired = 6,
}
