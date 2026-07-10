using LaraFashion.Models.Enums;

namespace LaraFashion.Models;

public class Product
{
    public Guid Id { get; set; }

    public string SerialNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;

    public decimal OriginalPrice { get; set; }

    public DiscountType DiscountType { get; set; }

    public decimal DiscountValue { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsPublished { get; set; } = true;

    public List<ProductSize> Sizes { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public decimal FinalPrice
    {
        get
        {
            return DiscountType switch
            {
                DiscountType.Percent => OriginalPrice - (OriginalPrice * DiscountValue / 100m),
                DiscountType.FixedPrice => DiscountValue,
                _ => OriginalPrice
            };
        }
    }



    public Discount? FixedSalePriceDiscount => ProductDiscounts
        .Where(x => x.Discount.IsActive &&
                    x.Discount.RuleType == DiscountRuleType.FixedSalePrice &&
                    x.Discount.Value > 0)
        .Select(x => x.Discount)
        .OrderBy(x => x.Value)
        .FirstOrDefault();

    public bool HasFixedSalePriceDiscount => FixedSalePriceDiscount is not null;

    public decimal StorePrice => FixedSalePriceDiscount?.Value ?? FinalPrice;

    public decimal StoreOriginalPrice => FinalPrice;

    public bool ShouldShowOldStorePrice => HasFixedSalePriceDiscount && StoreOriginalPrice != StorePrice;

    public List<ProductDiscount> ProductDiscounts { get; set; } = new();

    public List<ProductCategory> ProductCategories { get; set; } = new();
}
