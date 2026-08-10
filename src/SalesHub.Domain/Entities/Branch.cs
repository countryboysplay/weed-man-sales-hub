namespace SalesHub.Domain.Entities;

/// <summary>Branch/location (docs/01). Timezone is normally America/Chicago.</summary>
public class Branch
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "America/Chicago";
    public bool Active { get; set; } = true;
}
