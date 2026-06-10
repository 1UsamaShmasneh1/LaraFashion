namespace LaraFashion.Models;

public class PersistedCartState
{
    public DateTime LastUpdatedUtc { get; set; }

    public List<PersistedCartItem> Items { get; set; } = new();
}
