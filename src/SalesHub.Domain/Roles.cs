namespace SalesHub.Domain;

/// <summary>
/// The four application roles. Exactly these, per CLAUDE.md §2 — no additions.
/// </summary>
public static class Roles
{
    public const string SalesAgent = "SalesAgent";
    public const string SalesSupervisor = "SalesSupervisor";
    public const string SalesManager = "SalesManager";
    public const string Owner = "Owner";

    public static readonly IReadOnlyList<string> All =
        [SalesAgent, SalesSupervisor, SalesManager, Owner];

    /// <summary>"Management" = Supervisor + Manager + Owner unless a rule narrows it.</summary>
    public static readonly IReadOnlyList<string> Management =
        [SalesSupervisor, SalesManager, Owner];

    public static readonly IReadOnlyList<string> ManagerOrOwner =
        [SalesManager, Owner];

    public static bool IsValid(string role) => All.Contains(role, StringComparer.Ordinal);

    public static bool IsManagement(string role) =>
        Management.Contains(role, StringComparer.Ordinal);
}
