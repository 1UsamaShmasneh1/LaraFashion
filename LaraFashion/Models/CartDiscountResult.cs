namespace LaraFashion.Models;

public class CartDiscountResult
{
    public decimal OriginalTotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal FinalTotal => OriginalTotal - DiscountAmount;

    public string DiscountName { get; set; } = string.Empty;
}