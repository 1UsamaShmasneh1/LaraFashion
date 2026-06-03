namespace LaraFashion.Models;

public class ProductCategory
{
    public Guid ProductId { get; set; }

    public Product Product { get; set; } = new();

    public Guid CategoryId { get; set; }

    public Category Category { get; set; } = new();
}
