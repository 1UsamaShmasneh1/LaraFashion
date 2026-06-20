namespace LaraFashion.Models;

public class CartItem
{
    public Guid ProductId { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public string ProductSerialNumber { get; set; } = string.Empty;

    public string ProductImageUrl { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int MaxAvailableQuantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal OriginalUnitPrice { get; set; }

    public decimal TotalPrice => UnitPrice * Quantity;

    public decimal OriginalTotalPrice => OriginalUnitPrice * Quantity;

    public List<ProductDiscount> ProductDiscounts { get; set; } = new();

    public List<string> CategoryNames { get; set; } = new();

}