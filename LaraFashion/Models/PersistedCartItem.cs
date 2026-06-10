namespace LaraFashion.Models;

public class PersistedCartItem
{
    public Guid ProductId { get; set; }

    public string Size { get; set; } = string.Empty;

    public int Quantity { get; set; }
}
