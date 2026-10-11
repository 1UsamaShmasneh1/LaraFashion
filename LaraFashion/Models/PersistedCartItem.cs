namespace LaraFashion.Models;

public class PersistedCartItem
{
    public Guid ProductId { get; set; }

    public Guid? ProductSizeId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductSerialNumber { get; set; } = string.Empty;
    public string ProductImageUrl { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }

    public string Size { get; set; } = string.Empty;

    public int Quantity { get; set; }
}
