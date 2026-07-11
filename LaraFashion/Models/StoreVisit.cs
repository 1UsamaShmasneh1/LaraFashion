namespace LaraFashion.Models;

public class StoreVisit
{
    public Guid Id { get; set; }
    public string VisitorIdHash { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
}
