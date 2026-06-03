namespace LaraFashion.Models;

public class Category
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ProductCategory> ProductCategories { get; set; } = new();
}
