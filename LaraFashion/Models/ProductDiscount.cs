namespace LaraFashion.Models;

public class ProductDiscount
{
    public Guid ProductId { get; set; }

    public Product Product { get; set; } = new();

    public Guid DiscountId { get; set; }

    public Discount Discount { get; set; } = new();
}