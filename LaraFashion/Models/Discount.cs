using LaraFashion.Models.Enums;

namespace LaraFashion.Models;

public class Discount
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DiscountRuleType RuleType { get; set; }

    public DiscountValueType ValueType { get; set; }

    public decimal Value { get; set; }

    public int? MinimumQuantity { get; set; }

    public decimal? MinimumAmount { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<ProductDiscount> ProductDiscounts { get; set; } = new();

    public decimal? BundleFixedTotalPrice { get; set; }
}