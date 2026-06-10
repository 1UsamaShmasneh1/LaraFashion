using LaraFashion.Models;
using LaraFashion.Models.Enums;

namespace LaraFashion.Services;

public class CartService
{
    public List<CartItem> Items { get; private set; } = new();

    public event Action? OnChange;

    public decimal TotalPrice =>
        Items.Sum(x => x.TotalPrice);

    public int TotalItems =>
        Items.Sum(x => x.Quantity);

    public int GetQuantity(Guid productId, string size)
    {
        return Items.FirstOrDefault(x =>
            x.ProductId == productId &&
            x.Size == size)?.Quantity ?? 0;
    }

    public void AddToCart(Product product, string size, int quantity)
    {
        var maxAvailableQuantity = product.Sizes
            .FirstOrDefault(x => x.SizeName == size)?.Quantity ?? 0;

        if (maxAvailableQuantity <= 0)
            return;

        quantity = Math.Clamp(quantity, 1, maxAvailableQuantity);

        var existingItem = Items.FirstOrDefault(x =>
            x.ProductId == product.Id &&
            x.Size == size);

        if (existingItem is not null)
        {
            existingItem.ProductName = product.Name;
            existingItem.ProductSerialNumber = product.SerialNumber;
            existingItem.ProductImageUrl = product.ImageUrl;
            existingItem.UnitPrice = product.FinalPrice;
            existingItem.MaxAvailableQuantity = maxAvailableQuantity;
            existingItem.ProductDiscounts = product.ProductDiscounts;
            existingItem.CategoryNames = product.ProductCategories
                .Where(x => x.Category.IsActive)
                .Select(x => x.Category.Name)
                .ToList();

            existingItem.Quantity = quantity;
        }
        else
        {
            Items.Add(new CartItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                ProductSerialNumber = product.SerialNumber,
                ProductImageUrl = product.ImageUrl,
                Size = size,
                Quantity = quantity,
                MaxAvailableQuantity = maxAvailableQuantity,
                UnitPrice = product.FinalPrice,
                ProductDiscounts = product.ProductDiscounts,
                CategoryNames = product.ProductCategories
                    .Where(x => x.Category.IsActive)
                    .Select(x => x.Category.Name)
                    .ToList()
            });
        }

        NotifyStateChanged();
    }

    public void RemoveItem(CartItem item)
    {
        Items.Remove(item);

        NotifyStateChanged();
    }

    public void IncreaseQuantity(CartItem item)
    {
        if (item.MaxAvailableQuantity <= 0)
            return;

        if (item.Quantity < item.MaxAvailableQuantity)
        {
            item.Quantity++;
            NotifyStateChanged();
        }
    }

    public void DecreaseQuantity(CartItem item)
    {
        if (item.Quantity <= 1)
            return;

        item.Quantity--;

        NotifyStateChanged();
    }

    public List<PersistedCartItem> ToPersistedItems()
    {
        return Items
            .Select(x => new PersistedCartItem
            {
                ProductId = x.ProductId,
                Size = x.Size,
                Quantity = x.Quantity
            })
            .ToList();
    }

    public void ReplaceItems(List<CartItem> items)
    {
        Items = items;

        NotifyStateChanged();
    }

    public void Clear()
    {
        Items.Clear();

        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    public CartDiscountResult CalculateBestDiscount()
    {
        var originalTotal = Items.Sum(x => x.TotalPrice);

        var result = new CartDiscountResult
        {
            OriginalTotal = originalTotal,
            DiscountAmount = 0,
            DiscountName = string.Empty
        };

        var allDiscounts = Items
            .SelectMany(x => x.ProductDiscounts)
            .Select(x => x.Discount)
            .Where(x => x.IsActive)
            .DistinctBy(x => x.Id)
            .ToList();

        foreach (var discount in allDiscounts)
        {
            var relatedItems = Items
                .Where(item => item.ProductDiscounts.Any(pd => pd.DiscountId == discount.Id))
                .ToList();

            if (!relatedItems.Any())
                continue;

            var relatedQuantity = relatedItems.Sum(x => x.Quantity);
            var relatedTotal = relatedItems.Sum(x => x.TotalPrice);

            decimal discountAmount = 0;

            if (discount.RuleType == DiscountRuleType.ProductDirect)
            {
                discountAmount = CalculateValueDiscount(discount, relatedTotal);
            }
            else if (discount.RuleType == DiscountRuleType.QuantityBased &&
                     discount.MinimumQuantity.HasValue &&
                     relatedQuantity >= discount.MinimumQuantity.Value)
            {
                discountAmount = CalculateValueDiscount(discount, relatedTotal);
            }
            else if (discount.RuleType == DiscountRuleType.AmountBased &&
                     discount.MinimumAmount.HasValue &&
                     relatedTotal >= discount.MinimumAmount.Value)
            {
                discountAmount = CalculateValueDiscount(discount, relatedTotal);
            }
            else if (discount.RuleType == DiscountRuleType.BundleFixedPrice &&
         discount.MinimumQuantity.HasValue &&
         discount.BundleFixedTotalPrice.HasValue)
            {
                var requiredQty = discount.MinimumQuantity.Value;

                if (relatedQuantity >= requiredQty)
                {
                    var unitPrices = new List<decimal>();

                    foreach (var item in relatedItems)
                    {
                        for (int i = 0; i < item.Quantity; i++)
                        {
                            unitPrices.Add(item.UnitPrice);
                        }
                    }

                    unitPrices = unitPrices
                        .OrderByDescending(x => x)
                        .ToList();

                    var bundleCount = relatedQuantity / requiredQty;

                    decimal totalDiscount = 0;

                    for (int bundle = 0; bundle < bundleCount; bundle++)
                    {
                        var bundleItems = unitPrices
                            .Skip(bundle * requiredQty)
                            .Take(requiredQty)
                            .ToList();

                        var normalPrice = bundleItems.Sum();

                        var discountedPrice =
                            discount.BundleFixedTotalPrice.Value;

                        totalDiscount +=
                            Math.Max(0, normalPrice - discountedPrice);
                    }

                    discountAmount = totalDiscount;
                }
            }

            if (discountAmount > result.DiscountAmount)
            {
                result.DiscountAmount = discountAmount;
                result.DiscountName = discount.Name;
            }
        }

        return result;
    }

    private static decimal CalculateValueDiscount(Discount discount, decimal total)
    {
        if (discount.ValueType == DiscountValueType.Percent)
            return total * discount.Value / 100m;

        return discount.Value;
    }
}