using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class OrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Order>> GetOrdersAsync()
    {
        return await _db.Orders
            .Include(x => x.Customer)
            .Include(x => x.Items)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<Order> CreateOrderAsync(
    CustomerInfo customer,
    List<CartItem> cartItems,
    CartDiscountResult discountResult)
    {
        foreach (var item in cartItems)
        {
            var size = await _db.ProductSizes
                .FirstOrDefaultAsync(x =>
                    x.ProductId == item.ProductId &&
                    x.SizeName == item.Size);

            if (size is null || size.Quantity < item.Quantity)
            {
                throw new InvalidOperationException(
                    $"الكمية غير متوفرة للمنتج {item.ProductName}، المقاس {item.Size}.");
            }
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = $"ORD-{DateTime.Now:yyyyMMddHHmmss}",
            Customer = customer,
            Status = Models.Enums.OrderStatus.New,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,

            OriginalTotal = discountResult.OriginalTotal,
            DiscountAmount = discountResult.DiscountAmount,
            DiscountName = discountResult.DiscountName,
            FinalTotal = discountResult.FinalTotal,



            Items = cartItems.Select(item => new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                ProductSerialNumber = item.ProductSerialNumber,
                ProductImageUrl = item.ProductImageUrl,
                Size = item.Size,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice
            }).ToList()
        };

        _db.Orders.Add(order);

        foreach (var item in cartItems)
        {
            var size = await _db.ProductSizes
                .FirstAsync(x =>
                    x.ProductId == item.ProductId &&
                    x.SizeName == item.Size);

            size.Quantity -= item.Quantity;
        }

        await _db.SaveChangesAsync();

        return order;
    }

    public async Task<Order?> GetOrderAsync(Guid id)
    {
        return await _db.Orders
            .Include(x => x.Customer)
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task UpdateStatusAsync(Guid orderId, OrderStatus status)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(x => x.Id == orderId);

        if (order is null)
            return;

        order.Status = status;
        order.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();
    }

    private static string GenerateOrderNumber()
    {
        return $"LF-{DateTime.Now:yyyyMMddHHmmss}";
    }
}