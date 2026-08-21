using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public sealed record BulkOrderStatusResult(int UpdatedOrders);

public sealed record OrderCleanupResult(
    int EligibleOrders,
    int DeletedOrders,
    int DeletedOfficialOrders,
    int DeletedSandboxOrders,
    int DeletedReadyOrders,
    int DeletedCancelledOrders,
    DateTime ExecutedAt);

public class OrderService
{
    private readonly AppDbContext _db;
    private readonly AdminAuthService _adminAuthService;
    private readonly ILogger<OrderService> _logger;
    private readonly AppStoragePaths _storagePaths;
    private static readonly SemaphoreSlim CleanupLock = new(1, 1);

    public OrderService(
        AppDbContext db,
        AdminAuthService adminAuthService,
        ILogger<OrderService> logger,
        AppStoragePaths storagePaths)
    {
        _db = db;
        _adminAuthService = adminAuthService;
        _logger = logger;
        _storagePaths = storagePaths;
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
        var isSandboxOrder = false;

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

            isSandboxOrder |= !product.IsPublished;

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
            IsSandbox = isSandboxOrder,
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

        if (!order.IsSandbox)
        {
            _db.SalesHistory.Add(new SalesHistory
            {
                Id = Guid.NewGuid(),
                OriginalOrderId = order.Id,
                OrderNumber = order.OrderNumber,
                CreatedAtUtc = order.CreatedAt.ToUniversalTime(),
                CustomerName = customer.FullName,
                PhoneNumber = customer.PhoneNumber,
                TotalQuantity = cartItems.Sum(x => x.Quantity),
                FinalTotal = discountResult.FinalTotal,
                LastStatus = order.Status,
                StatusUpdatedAtUtc = order.UpdatedAt.ToUniversalTime()
            });
        }

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

        var sizes = await LoadProductSizeLookupAsync([order]);
        var histories = await LoadHistoryLookupAsync([order]);
        ApplyStatusChange(order, status, DateTime.Now, sizes, histories);

        await _db.SaveChangesAsync();
    }

    public async Task<BulkOrderStatusResult> UpdateStatusesAsync(
        IEnumerable<Guid> orderIds,
        OrderStatus status,
        string adminToken)
    {
        EnsureAdmin(adminToken);

        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("لم يتم تحديد أي طلبية.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var orders = await _db.Orders
                .Include(x => x.Items)
                .Where(x => ids.Contains(x.Id))
                .ToListAsync();

            if (orders.Count != ids.Count)
                throw new InvalidOperationException("تعذر العثور على إحدى الطلبيات المحددة. حدّث الصفحة وحاول مجدداً.");

            var sizes = await LoadProductSizeLookupAsync(orders);
            var histories = await LoadHistoryLookupAsync(orders);
            var updatedAt = DateTime.Now;

            foreach (var order in orders)
                ApplyStatusChange(order, status, updatedAt, sizes, histories);

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new BulkOrderStatusResult(orders.Count);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "Bulk order status update failed for {OrderCount} orders.", ids.Count);
            throw;
        }
    }

    public async Task<OrderCleanupResult> CleanOldOrdersAsync(string adminToken)
    {
        EnsureAdmin(adminToken);
        await CleanupLock.WaitAsync();

        try
        {
            var executedAt = DateTime.Now;
            var cutoff = executedAt.AddDays(-30);
            var eligibleQuery = _db.Orders.Where(x =>
                x.UpdatedAt <= cutoff &&
                (x.Status == OrderStatus.Ready || x.Status == OrderStatus.Cancelled));

            var orders = await eligibleQuery
                .Include(x => x.Customer)
                .Include(x => x.Items)
                .ToListAsync();

            var officialCount = orders.Count(x => !x.IsSandbox);
            var sandboxCount = orders.Count(x => x.IsSandbox);
            var readyCount = orders.Count(x => x.Status == OrderStatus.Ready);
            var cancelledCount = orders.Count(x => x.Status == OrderStatus.Cancelled);

            if (orders.Count == 0)
            {
                return new OrderCleanupResult(0, 0, 0, 0, 0, 0, executedAt);
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                _db.OrderItems.RemoveRange(orders.SelectMany(x => x.Items));
                _db.Customers.RemoveRange(orders.Select(x => x.Customer));
                _db.Orders.RemoveRange(orders);

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "Old order cleanup failed for {OrderCount} eligible orders.", orders.Count);
                throw;
            }

            return new OrderCleanupResult(
                orders.Count,
                orders.Count,
                officialCount,
                sandboxCount,
                readyCount,
                cancelledCount,
                executedAt);
        }
        finally
        {
            CleanupLock.Release();
        }
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

    private async Task<Dictionary<(Guid ProductId, string Size), ProductSize>> LoadProductSizeLookupAsync(
        IReadOnlyCollection<Order> orders)
    {
        var productIds = orders
            .SelectMany(x => x.Items)
            .Select(x => x.ProductId)
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
            return new Dictionary<(Guid, string), ProductSize>();

        return (await _db.ProductSizes
                .Where(x => productIds.Contains(x.ProductId))
                .ToListAsync())
            .ToDictionary(x => (x.ProductId, x.SizeName));
    }

    private async Task<Dictionary<Guid, SalesHistory>> LoadHistoryLookupAsync(IReadOnlyCollection<Order> orders)
    {
        var orderIds = orders
            .Where(x => !x.IsSandbox)
            .Select(x => x.Id)
            .ToList();

        if (orderIds.Count == 0)
            return new Dictionary<Guid, SalesHistory>();

        return (await _db.SalesHistory
                .Where(x => x.OriginalOrderId.HasValue && orderIds.Contains(x.OriginalOrderId.Value))
                .ToListAsync())
            .Where(x => x.OriginalOrderId.HasValue)
            .ToDictionary(x => x.OriginalOrderId!.Value);
    }

    private static void ApplyStatusChange(
        Order order,
        OrderStatus status,
        DateTime updatedAt,
        IReadOnlyDictionary<(Guid ProductId, string Size), ProductSize> sizes,
        IReadOnlyDictionary<Guid, SalesHistory> histories)
    {
        var oldStatus = order.Status;

        foreach (var item in order.Items)
        {
            sizes.TryGetValue((item.ProductId, item.Size), out var size);

            if (oldStatus != OrderStatus.Cancelled && status == OrderStatus.Cancelled)
            {
                if (size is not null)
                    size.Quantity += item.Quantity;
            }
            else if (oldStatus == OrderStatus.Cancelled && status != OrderStatus.Cancelled)
            {
                if (size is null || size.Quantity < item.Quantity)
                {
                    throw new InvalidOperationException(
                        $"لا يمكن تغيير حالة الطلبية، الكمية غير متوفرة للمنتج {item.ProductName}، المقاس {item.Size}.");
                }

                size.Quantity -= item.Quantity;
            }
        }

        order.Status = status;
        order.UpdatedAt = updatedAt;

        if (!order.IsSandbox && histories.TryGetValue(order.Id, out var history))
        {
            history.LastStatus = status;
            history.StatusUpdatedAtUtc = updatedAt.ToUniversalTime();
        }
    }

    private void EnsureAdmin(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_adminAuthService.IsTokenValid(token))
            throw new UnauthorizedAccessException("انتهت صلاحية جلسة الإدارة.");
    }

    private async Task DeleteImageIfUnusedAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.StartsWith("/uploads/"))
            return;

        var usedByProduct = await _db.Products.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByProductImage = await _db.ProductImages.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByOrder = await _db.OrderItems.AnyAsync(x => x.ProductImageUrl == imageUrl);

        if (usedByProduct || usedByProductImage || usedByOrder)
            return;

        var fileName = Path.GetFileName(imageUrl);
        var filePath = Path.Combine(_storagePaths.UploadsPath, fileName);

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
