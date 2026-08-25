namespace LaraFashion.Models;

public sealed class StoreChatSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool IsCustomerChatEnabled { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
