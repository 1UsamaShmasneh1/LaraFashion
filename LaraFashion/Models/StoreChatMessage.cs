namespace LaraFashion.Models;

public class StoreChatMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public StoreChatSession Session { get; set; } = null!;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string? DetectedIntent { get; set; }
    public string? ExtractedSize { get; set; }
    public bool AiUsed { get; set; }
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public bool IsFallback { get; set; }
    public string? FallbackReason { get; set; }
}
