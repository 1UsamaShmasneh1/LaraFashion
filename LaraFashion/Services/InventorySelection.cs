using LaraFashion.Models;

namespace LaraFashion.Services;

public static class InventorySelection
{
    public static ProductSize Resolve(Product product, Guid? sizeId, string label)
    {
        if (!SizeNamePolicy.IsValid(label))
            throw new InventoryException(InventoryFailureReason.SizeMissing, "المقاس غير صالح. احذف العنصر وأعد اختيار المقاس من المنتج.");
        var key = SizeNamePolicy.Key(label);
        ProductSize? size = null;
        if (sizeId.HasValue)
        {
            size = product.Sizes.SingleOrDefault(s => s.Id == sizeId);
            if (size is null)
                throw new InventoryException(InventoryFailureReason.StaleIdentity, "المقاس المختار حُذف أو استُبدل. احذف العنصر وأعد اختياره من المنتج.");
            if (SizeNamePolicy.Key(size.SizeName) != key)
                throw new InventoryException(InventoryFailureReason.SizeChanged, "تغير المقاس المختار. احذف العنصر وأعد اختيار المقاس من المنتج.");
        }
        var matches = product.Sizes.Where(s => SizeNamePolicy.Key(s.SizeName) == key).ToList();
        if (matches.Count > 1)
            throw new InventoryException(InventoryFailureReason.AmbiguousSize, "بيانات هذا المقاس مكررة. اختر مقاساً آخر أو تواصل مع المتجر؛ بقية السلة محفوظة.");
        if (matches.Count == 0)
            throw new InventoryException(InventoryFailureReason.SizeMissing, "المقاس لم يعد موجوداً. احذف العنصر وأعد اختيار المقاس من المنتج.");
        return size ?? matches[0];
    }

    public static int TotalQuantity(IEnumerable<int> quantities)
    {
        var total = quantities.Sum(q => (long)q);
        if (total is < 1 or > int.MaxValue)
            throw new InventoryException(InventoryFailureReason.InvalidQuantity, "الكمية المطلوبة غير صالحة. راجع الكميات في السلة.");
        return (int)total;
    }
}
