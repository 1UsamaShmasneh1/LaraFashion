namespace LaraFashion.Models;

public class StoreChatSession
{
    public Guid Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
    public string? PhoneNumber { get; set; }
    public List<StoreChatMessage> Messages { get; set; } = new();
}
