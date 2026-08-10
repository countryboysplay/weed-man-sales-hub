namespace SalesHub.Api.Auth;

public static class AuthConstants
{
    public const string CookieScheme = "SalesHub.Cookie";
    public const string CookieName = ".SalesHub.Auth";

    public const string SessionIdClaim = "sh:session_id";
    public const string SessionVerifierClaim = "sh:session_verifier";

    /// <summary>HttpContext.Items key for the validated UserSession row.</summary>
    public const string SessionItemKey = "SalesHub.CurrentSession";
    public const string UserRoleItemKey = "SalesHub.CurrentRole";
}

public static class Policies
{
    public const string Employee = "Employee";
    public const string Management = "Management";
    public const string SupervisorOrAbove = "SupervisorOrAbove";
    public const string ManagerOrOwner = "ManagerOrOwner";
    public const string OwnerOnly = "OwnerOnly";
    public const string FreshAuthRequired = "FreshAuthRequired";
    public const string MonitoredWorkSession = "MonitoredWorkSession";
}
