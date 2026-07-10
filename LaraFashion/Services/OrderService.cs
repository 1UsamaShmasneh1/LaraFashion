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
        CartDiscountResult discountResult,
        bool allowUnpublished = false)
    {
        foreach (var item in cartItems)
        {
            var product = await _db.Products
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == item.ProductId);

            if (product is null || !product.IsActive || (!product.IsPublished && !allowUnpublished))
            {
                throw new InvalidOperationException(
                    $"المنتج {item.ProductName} غير متاح لإرسال الطلب.");
            }

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
            Status = OrderStatus.New,
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
        var order = await _db.Orders
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == orderId);

        if (order is null)
            return;

        var oldStatus = order.Status;

        if (oldStatus != OrderStatus.Cancelled && status == OrderStatus.Cancelled)
        {
            await RestoreOrderQuantitiesAsync(order);
        }
        else if (oldStatus == OrderStatus.Cancelled && status != OrderStatus.Cancelled)
        {
            await DecreaseOrderQuantitiesAsync(order);
        }

        order.Status = status;
        order.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteOrderAsync(Guid orderId)
    {
        var order = await _db.Orders
            .Include(x => x.Customer)
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == orderId);

        if (order is null)
            return;

        if (order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Ready)
        {
            throw new InvalidOperationException("يمكن حذف الطلبية فقط إذا كانت بحالة ملغاة أو جاهز.");
        }

        var imageUrls = order.Items
            .Select(x => x.ProductImageUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        _db.OrderItems.RemoveRange(order.Items);
        _db.Customers.Remove(order.Customer);
        _db.Orders.Remove(order);

        await _db.SaveChangesAsync();

        foreach (var imageUrl in imageUrls)
        {
            await DeleteImageIfUnusedAsync(imageUrl);
        }
    }

    private async Task RestoreOrderQuantitiesAsync(Order order)
    {
        foreach (var item in order.Items)
        {
            var size = await _db.ProductSizes
                .FirstOrDefaultAsync(x =>
                    x.ProductId == item.ProductId &&
                    x.SizeName == item.Size);

            if (size is not null)
            {
                size.Quantity += item.Quantity;
            }
        }
    }

    private async Task DecreaseOrderQuantitiesAsync(Order order)
    {
        foreach (var item in order.Items)
        {
            var size = await _db.ProductSizes
                .FirstOrDefaultAsync(x =>
                    x.ProductId == item.ProductId &&
                    x.SizeName == item.Size);

            if (size is null || size.Quantity < item.Quantity)
            {
                throw new InvalidOperationException(
                    $"لا يمكن تغيير حالة الطلبية، الكمية غير متوفرة للمنتج {item.ProductName}، المقاس {item.Size}.");
            }

            size.Quantity -= item.Quantity;
        }
    }

    private async Task DeleteImageIfUnusedAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.StartsWith("/uploads/"))
            return;

        var usedByProduct = await _db.Products.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByOrder = await _db.OrderItems.AnyAsync(x => x.ProductImageUrl == imageUrl);

        if (usedByProduct || usedByOrder)
            return;

        var fileName = Path.GetFileName(imageUrl);
        var filePath = Path.Combine("/var/www/larafashion/uploads", fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string GenerateOrderNumber()
    {
        return $"LF-{DateTime.Now:yyyyMMddHHmmss}";
    }
}
