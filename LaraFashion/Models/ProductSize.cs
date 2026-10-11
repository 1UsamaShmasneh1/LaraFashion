namespace LaraFashion.Models;

public class ProductSize
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public string SizeName { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public int Quantity { get; set; }
    public long ReviewRevision { get; set; }
}
