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

    public async Task<Order> CreateOrderAsync(CustomerInfo customer, List<CartItem> cartItems)
    {
        var now = DateTime.Now;

        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = GenerateOrderNumber(),
            Customer = customer,
            Status = OrderStatus.New,
            CreatedAt = now,
            UpdatedAt = now,
            Items = cartItems.Select(x => new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = x.ProductId,
                ProductName = x.ProductName,
                ProductSerialNumber = x.ProductSerialNumber,
                ProductImageUrl = x.ProductImageUrl,
                Size = x.Size,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice
            }).ToList()
        };

        _db.Orders.Add(order);
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