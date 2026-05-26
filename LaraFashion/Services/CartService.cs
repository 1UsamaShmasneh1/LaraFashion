using LaraFashion.Models;

namespace LaraFashion.Services;

public class CartService
{
    public List<CartItem> Items { get; private set; } = new();

    public event Action? OnChange;

    public decimal TotalPrice =>
        Items.Sum(x => x.TotalPrice);

    public int TotalItems =>
        Items.Sum(x => x.Quantity);

    public void AddToCart(Product product, string size, int quantity)
    {
        var existingItem = Items.FirstOrDefault(x =>
            x.ProductId == product.Id &&
            x.Size == size);

        if (existingItem is not null)
        {
            existingItem.Quantity += quantity;
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
                UnitPrice = product.FinalPrice
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
        item.Quantity++;

        NotifyStateChanged();
    }

    public void DecreaseQuantity(CartItem item)
    {
        if (item.Quantity <= 1)
            return;

        item.Quantity--;

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
}