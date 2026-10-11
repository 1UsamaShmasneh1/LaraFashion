using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace LaraFashion.Services;

public sealed record BulkOrderStatusResult(int UpdatedOrders);
public sealed record CheckoutReceipt(Guid OrderId, string OrderNumber);

public sealed record PublicOrderLookupResult(IReadOnlyList<Order> OpenOrders, Order? ClosedOrder, SalesHistory? History)
{
    public bool HasAny => OpenOrders.Count > 0 || ClosedOrder is not null || History is not null;
}

public sealed record OrderCleanupResult(
    int EligibleOrders,
    int DeletedOrders,
    int DeletedOfficialOrders,
    int DeletedSandboxOrders,
    int DeletedReadyOrders,
    int DeletedCancelledOrders,
    DateTime ExecutedAt);

public partial class OrderService
{
    private readonly AdminAuthService _adminAuthService;
    private readonly AppStoragePaths _storagePaths;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<OrderService> _logger;
    private readonly IDataProtector? _reviewProtector;
    private static readonly SemaphoreSlim CleanupLock = new(1, 1);

    public OrderService(
        AdminAuthService adminAuthService,
        AppStoragePaths storagePaths,
        IDbContextFactory<AppDbContext> factory, ILogger<OrderService>? logger = null,
        IDataProtectionProvider? dataProtection = null)
    {
        _adminAuthService = adminAuthService;
        _storagePaths = storagePaths;
        _factory = factory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderService>.Instance;
        _reviewProtector = dataProtection?.CreateProtector("LaraFashion.OrderInventoryReview.V1");
    }

    public async Task<List<Order>> GetOrdersAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Orders
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Items)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<CheckoutReceipt?> GetCheckoutReceiptAsync(Guid attemptId)
    {
        // The opaque per-attempt capability returns no customer or order contents.
        await using var db = await _factory.CreateDbContextAsync();
        var attempt = await db.CheckoutAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId);
        if (attempt is null) return null;
        var number = await db.Orders.Where(x => x.Id == attempt.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync()
            ?? await db.SalesHistory.Where(x => x.OriginalOrderId == attempt.OrderId).Select(x => x.OrderNumber).SingleOrDefaultAsync()
            ?? await db.OrderNumberAudits.Where(x => x.OrderId == attempt.OrderId).OrderByDescending(x => x.ChangedAtUtc)
                .Select(x => x.NewNumber).FirstOrDefaultAsync()
            ?? await db.OrderNumberReservations.Where(x => x.OrderId == attempt.OrderId).Select(x => x.Number).FirstAsync();
        return new(attempt.OrderId, number);
    }

    public Task<Order> CreateOrderAsync(
        CustomerInfo customer,
        List<CartItem> cartItems,
        CartDiscountResult discountResult,
        bool allowUnpublished = false,
        Guid? checkoutAttemptId = null)
    {
        // Capture UI input before the first await; Blazor may process other events while saving.
        customer = new CustomerInfo { FullName = customer.FullName, PhoneNumber = customer.PhoneNumber,
            City = customer.City, Street = customer.Street, HouseNumber = customer.HouseNumber };
        cartItems = cartItems.Select(x => new CartItem { ProductId = x.ProductId, ProductSizeId = x.ProductSizeId, ProductName = x.ProductName,
            ProductSerialNumber = x.ProductSerialNumber, ProductImageUrl = x.ProductImageUrl,
            Size = x.Size, Quantity = x.Quantity, UnitPrice = x.UnitPrice }).ToList();
        discountResult = new CartDiscountResult { OriginalTotal = discountResult.OriginalTotal,
            DiscountAmount = discountResult.DiscountAmount, DiscountName = discountResult.DiscountName };
        var correlation = Guid.NewGuid();
        if (cartItems.Count == 0)
            throw Failure(InventoryFailureReason.InvalidQuantity, "السلة فارغة. أضف منتجاً قبل إرسال الطلب.", correlation, null, null, 0);
        var invalidItem = cartItems.FirstOrDefault(x => x.Quantity <= 0 || !SizeNamePolicy.IsValid(x.Size));
        if (invalidItem is not null)
            throw Failure(invalidItem.Quantity <= 0 ? InventoryFailureReason.InvalidQuantity : InventoryFailureReason.SizeMissing,
                "السلة تحتوي كمية أو مقاساً غير صالح. راجع هذا العنصر قبل إرسال الطلب.", correlation, invalidItem.ProductId, invalidItem.ProductSizeId, invalidItem.Quantity);
        if (checkoutAttemptId == Guid.Empty) throw new InvalidOperationException("محاولة الشراء غير صالحة.");
        var legacyRequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            customer.FullName,
            customer.PhoneNumber,
            customer.City,
            customer.Street,
            customer.HouseNumber,
            Items = cartItems.OrderBy(x => x.ProductId).ThenBy(x => x.Size).ThenBy(x => x.Quantity).ThenBy(x => x.UnitPrice)
                .Select(x => new { x.ProductId, x.Size, x.Quantity, x.UnitPrice }),
            discountResult.OriginalTotal,
            discountResult.DiscountAmount,
            discountResult.DiscountName,
            allowUnpublished
        }))));
        var requestHash = "V2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyRequestHash
            + JsonSerializer.Serialize(cartItems.OrderBy(x => x.ProductId).ThenBy(x => x.Size).ThenBy(x => x.Quantity).ThenBy(x => x.UnitPrice)
                .Select(x => x.ProductSizeId)))));
        return WriteInventoryAsync(async db =>
        {
            if (checkoutAttemptId.HasValue)
            {
                var attempt = await db.CheckoutAttempts.FindAsync(checkoutAttemptId.Value);
                if (attempt is not null)
                {
                    if (attempt.RequestHash != requestHash && attempt.RequestHash != legacyRequestHash)
                        throw new InvalidOperationException("محاولة الشراء هذه تخص سلة أخرى. افتح محاولة جديدة بعد مراجعة الطلب السابق.");
                    return await db.Orders.Include(x => x.Customer).Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == attempt.OrderId)
                        ?? throw new InvalidOperationException("تم إرسال هذه المحاولة سابقاً وأرشفة الطلب. راجع طلباتي؛ لن يخصم المخزون مجدداً.");
                }
            }
            var isSandboxOrder = false;
            foreach (var item in cartItems)
            {
                var product = await db.Products.AsNoTracking().Include(p => p.Sizes).SingleOrDefaultAsync(x => x.Id == item.ProductId);
                if (product is null || !product.IsActive || (!product.IsPublished && !allowUnpublished))
                    throw Failure(InventoryFailureReason.ProductUnavailable, "المنتج لم يعد متاحاً. راجع هذا العنصر في السلة.", correlation, item.ProductId, item.ProductSizeId, item.Quantity);
                isSandboxOrder |= !product.IsPublished;
                try { item.ProductSizeId = InventorySelection.Resolve(product, item.ProductSizeId, item.Size).Id; }
                catch (InventoryException ex)
                { throw Failure(ex.Reason, ex.Message, correlation, item.ProductId, item.ProductSizeId, item.Quantity,
                    product.Sizes.Count(s => SizeNamePolicy.Key(s.SizeName) == SizeNamePolicy.Key(item.Size))); }
            }
            foreach (var group in cartItems.GroupBy(x => (x.ProductId, x.ProductSizeId)))
            {
                var quantity = InventorySelection.TotalQuantity(group.Select(x => x.Quantity));
                var changed = await db.ProductSizes
                    .Where(x => x.ProductId == group.Key.ProductId && x.Id == group.Key.ProductSizeId && x.Quantity >= quantity)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - quantity));
                if (changed != 1)
                    throw Failure(InventoryFailureReason.InsufficientStock, $"الكمية المطلوبة غير متاحة للمقاس {group.First().Size}. راجع الكمية في السلة.",
                        correlation, group.Key.ProductId, group.Key.ProductSizeId, quantity, 1, changed);
            }
            var orderId = Guid.NewGuid();
            for (var retry = 0; await db.OrderNumberReservations.AnyAsync(x => x.OrderId == orderId); retry++)
            {
                if (retry == 8) throw new InvalidOperationException("تعذر تخصيص هوية الطلب. حاول مجدداً.");
                orderId = Guid.NewGuid();
            }
            var number = $"ORD-{orderId:N}".ToUpperInvariant();
            // A manual edit may have reserved a future generated number. Never overwrite it.
            for (var retry = 0; await db.OrderNumberReservations.AnyAsync(x => x.Number == number); retry++)
            {
                if (retry == 8) throw new InvalidOperationException("تعذر تخصيص رقم الطلب. حاول مجدداً.");
                number = $"ORD-{Guid.NewGuid():N}".ToUpperInvariant();
            }
            db.OrderNumberReservations.Add(new() { Number = number, OrderId = orderId });
            await db.SaveChangesAsync();
            var order = new Order
            {
                Id = orderId,
                OrderNumber = number,
                Customer = new CustomerInfo
                {
                    Id = Guid.NewGuid(),
                    FullName = customer.FullName,
                    PhoneNumber = customer.PhoneNumber,
                    City = customer.City,
                    Street = customer.Street,
                    HouseNumber = customer.HouseNumber
                },
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
                    ProductSizeId = item.ProductSizeId,
                    ProductName = item.ProductName,
                    ProductSerialNumber = item.ProductSerialNumber,
                    ProductImageUrl = item.ProductImageUrl,
                    Size = item.Size,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice
                }).ToList()
            };

            db.Orders.Add(order);

            if (!order.IsSandbox)
            {
                db.SalesHistory.Add(new SalesHistory
                {
                    Id = Guid.NewGuid(),
                    OriginalOrderId = order.Id,
                    OrderNumber = order.OrderNumber,
                    CreatedAtUtc = order.CreatedAt.ToUniversalTime(),
                    CustomerName = customer.FullName,
                    PhoneNumber = customer.PhoneNumber,
                    TotalQuantity = InventorySelection.TotalQuantity(cartItems.Select(x => x.Quantity)),
                    FinalTotal = discountResult.FinalTotal,
                    LastStatus = order.Status,
                    StatusUpdatedAtUtc = order.UpdatedAt.ToUniversalTime()
                });
            }

            if (checkoutAttemptId.HasValue)
                db.CheckoutAttempts.Add(new() { Id = checkoutAttemptId.Value, OrderId = order.Id, RequestHash = requestHash });
            return order;
        }, correlation, cartItems);
    }

    public async Task<Order?> GetOrderAsync(Guid id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Orders
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PublicOrderLookupResult> LookupPublicOrdersByPhoneAsync(string phoneNumber)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var normalized = PhoneNumberNormalizer.Normalize(phoneNumber);
        if (normalized is null)
            return new([], null, null);

        var phoneCandidates = await db.Orders.AsNoTracking()
            .Where(x => !x.IsSandbox)
            .Select(x => new { x.Id, x.Customer.PhoneNumber })
            .ToListAsync();
        var matchingOrderIds = phoneCandidates
            .Where(x => PhoneNumberNormalizer.Normalize(x.PhoneNumber) == normalized)
            .Select(x => x.Id)
            .ToList();

        var matches = await db.Orders.AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Customer)
            .Include(x => x.Items)
            .Where(x => matchingOrderIds.Contains(x.Id))
            .ToListAsync();
        var openOrders = matches
            .Where(x => x.Status is not OrderStatus.Ready and not OrderStatus.Cancelled)
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        if (openOrders.Count > 0)
            return new(openOrders, null, null);

        var closedOrder = matches
            .Where(x => x.Status is OrderStatus.Ready or OrderStatus.Cancelled)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefault();
        if (closedOrder is not null)
            return new([], closedOrder, null);

        var historyCandidates = await db.SalesHistory.AsNoTracking()
            .Where(x => x.LastStatus == OrderStatus.Ready || x.LastStatus == OrderStatus.Cancelled)
            .Select(x => new { x.Id, x.PhoneNumber })
            .ToListAsync();
        var matchingHistoryIds = historyCandidates
            .Where(x => PhoneNumberNormalizer.Normalize(x.PhoneNumber) == normalized)
            .Select(x => x.Id)
            .ToList();
        var history = await db.SalesHistory.AsNoTracking()
            .Where(x => matchingHistoryIds.Contains(x.Id))
            .OrderByDescending(x => x.StatusUpdatedAtUtc)
            .FirstOrDefaultAsync();
        return new([], null, history);
    }

    public async Task<CustomerOrderIdentityResult> LookupCustomerOrderIdentityAsync(
        string phoneNumber,
        bool includeSandbox,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (includeSandbox)
            EnsureAdmin(adminToken ?? string.Empty);

        var normalized = PhoneNumberNormalizer.Normalize(phoneNumber);
        if (normalized is null)
            return new(false, string.Empty, null);

        var orderCandidates = await db.Orders.AsNoTracking()
            .Where(x => includeSandbox || !x.IsSandbox)
            .Select(x => new
            {
                x.Customer.PhoneNumber,
                x.Customer.FullName,
                x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var historyCandidates = await db.SalesHistory.AsNoTracking()
            .Select(x => new
            {
                x.PhoneNumber,
                x.CustomerName,
                x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var latestOrderIdentity = orderCandidates
            .Where(x => PhoneNumberNormalizer.Normalize(x.PhoneNumber) == normalized)
            .Select(x => new CustomerIdentityCandidate(x.FullName, x.CreatedAt))
            .Concat(historyCandidates
                .Where(x => PhoneNumberNormalizer.Normalize(x.PhoneNumber) == normalized)
                .Select(x => new CustomerIdentityCandidate(
                    x.CustomerName,
                    ReportsService.ToJerusalem(x.CreatedAtUtc))))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();

        return latestOrderIdentity is null
            ? new(false, string.Empty, normalized)
            : new(true, latestOrderIdentity.CustomerName, normalized);
    }

    public async Task<CustomerOrdersResult> GetCustomerOrdersByPhoneAsync(
        string phoneNumber,
        bool includeSandbox,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (includeSandbox)
            EnsureAdmin(adminToken ?? string.Empty);

        var normalized = PhoneNumberNormalizer.Normalize(phoneNumber)
            ?? throw new ArgumentException("رقم الهاتف غير صالح.", nameof(phoneNumber));

        var phoneCandidates = await db.Orders.AsNoTracking()
            .Where(x => includeSandbox || !x.IsSandbox)
            .Select(x => new { x.Id, x.Customer.PhoneNumber })
            .ToListAsync(cancellationToken);
        var matchingOrderIds = phoneCandidates
            .Where(x => PhoneNumberNormalizer.Normalize(x.PhoneNumber) == normalized)
            .Select(x => x.Id)
            .ToList();

        var liveOrders = matchingOrderIds.Count == 0
            ? []
            : await db.Orders.AsNoTracking()
                .AsSplitQuery()
                .Include(x => x.Customer)
                .Include(x => x.Items)
                .Where(x => matchingOrderIds.Contains(x.Id))
                .ToListAsync(cancellationToken);

        var historyPhoneCandidates = await db.SalesHistory.AsNoTracking()
            .Select(x => new { x.Id, x.PhoneNumber })
            .ToListAsync(cancellationToken);
        var matchingHistoryIds = historyPhoneCandidates
            .Where(x => PhoneNumberNormalizer.Normalize(x.PhoneNumber) == normalized)
            .Select(x => x.Id)
            .ToList();
        var histories = matchingHistoryIds.Count == 0
            ? []
            : await db.SalesHistory.AsNoTracking()
                .Where(x => matchingHistoryIds.Contains(x.Id))
                .ToListAsync(cancellationToken);

        var liveOrderIds = liveOrders.Select(x => x.Id).ToHashSet();
        var orders = liveOrders
            .Select(ToCustomerOrderSummary)
            .Concat(histories
                .Where(x => !x.OriginalOrderId.HasValue || !liveOrderIds.Contains(x.OriginalOrderId.Value))
                .Select(ToHistoricalCustomerOrderSummary))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();

        var customerName = orders.FirstOrDefault()?.CustomerName ?? string.Empty;
        return new(customerName, normalized, orders);
    }

    public Task UpdateStatusAsync(Guid orderId, OrderStatus status, string adminToken, long? expectedRevision = null)
    {
        EnsureAdmin(adminToken);
        return ChangeStatusesAsync([orderId], status, adminToken, expectedRevision.HasValue
            ? new Dictionary<Guid, long> { [orderId] = expectedRevision.Value } : null);
    }

    public async Task<BulkOrderStatusResult> UpdateStatusesAsync(
        IEnumerable<Guid> orderIds,
        OrderStatus status,
        string adminToken, IReadOnlyDictionary<Guid, long>? expectedRevisions = null)
    {
        EnsureAdmin(adminToken);

        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("لم يتم تحديد أي طلبية.");

        return new BulkOrderStatusResult(await ChangeStatusesAsync(ids, status, adminToken, expectedRevisions?.ToDictionary(x => x.Key, x => x.Value)));
    }

    private Task<int> ChangeStatusesAsync(List<Guid> ids, OrderStatus status, string adminToken, IReadOnlyDictionary<Guid, long>? expectedRevisions = null) =>
        WriteInventoryAsync(async db =>
        {
            EnsureAdmin(adminToken);
            if (!Enum.IsDefined(status)) throw new InvalidOperationException("حالة الطلب غير صالحة.");
            var orders = await db.Orders.Include(x => x.Items).Where(x => ids.Contains(x.Id)).ToListAsync();
            if (orders.Count != ids.Count) throw new InvalidOperationException("تعذر العثور على إحدى الطلبيات. حدّث الصفحة.");
            var needs = new List<OrderInventoryReviewNeed>();
            foreach (var order in orders)
            {
                if (!Enum.IsDefined(order.Status) || order.InventoryRevision < 0 || order.InventoryRevision == long.MaxValue)
                    throw new InvalidOperationException("حالة الطلب الحالية أو إصدارها غير صالح؛ يلزم فحص مستقل.");
                if (order.Status == status) continue;
                if (expectedRevisions is not null && (!expectedRevisions.TryGetValue(order.Id, out var revision)
                    || revision != order.InventoryRevision)) throw ReviewConflict();
                if (NeedsInventory(order.Status, status))
                {
                    ValidateInventoryOrder(order);
                    if (order.Items.Any(i => i.ProductSizeId is null)) needs.Add(await BuildReviewNeedAsync(db, order, status));
                }
            }
            if (needs.Count > 0) throw new InventoryReviewRequiredException(needs);
            var updated = 0;
            foreach (var order in orders)
            {
                if (order.Status == status) continue;
                if (order.Status == OrderStatus.Cancelled || status == OrderStatus.Cancelled)
                foreach (var group in order.Items.GroupBy(x => (x.ProductId, x.ProductSizeId)))
                {
                    var correlation = Guid.NewGuid();
                    var quantity = InventorySelection.TotalQuantity(group.Select(x => x.Quantity));
                    if (group.Key.ProductSizeId is null)
                        throw Failure(InventoryFailureReason.HistoricalIdentityUnknown,
                            "تعذر تعديل المخزون تلقائياً: سطر طلب قديم بلا هوية مقاس موثوقة. يلزم مراجعة إدارية؛ لم تتغير الحالة أو الكميات.",
                            correlation, group.Key.ProductId, null, quantity);
                    var product = await db.Products.AsNoTracking().Include(p => p.Sizes).SingleOrDefaultAsync(p => p.Id == group.Key.ProductId);
                    if (product is null)
                        throw Failure(InventoryFailureReason.ProductUnavailable, "تعذر تعديل المخزون: المنتج لم يعد موجوداً. يلزم مراجعة إدارية.", correlation, group.Key.ProductId, group.Key.ProductSizeId, quantity);
                    foreach (var item in group)
                    {
                        try { InventorySelection.Resolve(product, item.ProductSizeId, item.Size); }
                        catch (InventoryException ex) { throw Failure(ex.Reason, ex.Message, correlation, item.ProductId, item.ProductSizeId, quantity,
                            product.Sizes.Count(s => SizeNamePolicy.Key(s.SizeName) == SizeNamePolicy.Key(item.Size))); }
                    }
                    var sizes = db.ProductSizes.Where(x => x.ProductId == group.Key.ProductId && x.Id == group.Key.ProductSizeId);
                    int changed;
                    if (order.Status != OrderStatus.Cancelled && status == OrderStatus.Cancelled)
                        changed = await sizes.Where(x => x.Quantity <= int.MaxValue - quantity)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + quantity));
                    else changed = await sizes.Where(x => x.Quantity >= quantity)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - quantity));
                    if (changed != 1)
                        throw Failure(InventoryFailureReason.InsufficientStock, "لا يمكن تغيير حالة الطلبية: الكمية غير متاحة أو تتجاوز الحد المسموح. راجع المخزون.",
                            correlation, group.Key.ProductId, group.Key.ProductSizeId, quantity, 1, changed);
                }
                order.Status = status;
                order.InventoryRevision = checked(order.InventoryRevision + 1);
                order.UpdatedAt = DateTime.Now;
                if (!order.IsSandbox)
                    await db.SalesHistory.Where(x => x.OriginalOrderId == order.Id).ExecuteUpdateAsync(s =>
                        s.SetProperty(x => x.LastStatus, status).SetProperty(x => x.StatusUpdatedAtUtc, order.UpdatedAt.ToUniversalTime()));
                updated++;
            }
            return updated;
        }, Guid.NewGuid());

    public Task<bool> EditOrderNumberAsync(Guid orderId, string newNumber, int expectedRevision, string reason, string adminToken)
    {
        var admin = _adminAuthService.GetAdminIdentity(adminToken)
            ?? throw new UnauthorizedAccessException("انتهت صلاحية جلسة الإدارة.");
        var normalized = OrderNumberPolicy.Normalize(newNumber);
        reason = (reason ?? string.Empty).Trim();
        if (reason.Length is < 1 or > 500) throw new InvalidOperationException("سبب التعديل مطلوب، بحد أقصى 500 حرف.");
        return SqliteWork.WriteAsync(_factory, async db =>
        {
            var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId)
                ?? throw new InvalidOperationException("الطلب لم يعد موجوداً. حدّث الصفحة.");
            if (order.NumberRevision != expectedRevision)
                throw new InvalidOperationException("تغير رقم الطلب أثناء فتح النافذة. أغلقها وافتح الطلب مجدداً.");
            if (OrderNumberPolicy.Normalize(order.OrderNumber) == normalized) return false;
            var reservation = await db.OrderNumberReservations.FindAsync(normalized);
            if (reservation is not null && reservation.OrderId != order.Id)
                throw new InvalidOperationException("رقم الطلب محجوز لطلب آخر، حالي أو تاريخي أو تجريبي.");
            if (reservation is null)
            {
                db.OrderNumberReservations.Add(new() { Number = normalized, OrderId = order.Id });
                await db.SaveChangesAsync();
            }
            db.OrderNumberAudits.Add(new()
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                PreviousNumber = order.OrderNumber,
                NewNumber = normalized,
                AdminIdentity = admin,
                ChangedAtUtc = DateTime.UtcNow,
                Reason = reason
            });
            order.OrderNumber = normalized;
            order.NumberRevision++;
            // Only the display field changes. Financial snapshots and cleanup timestamps stay intact.
            await db.SalesHistory.Where(x => x.OriginalOrderId == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrderNumber, normalized));
            return true;
        });
    }

    public async Task<OrderCleanupResult> CleanOldOrdersAsync(string adminToken)
    {
        EnsureAdmin(adminToken);
        await CleanupLock.WaitAsync();

        try
        {
            var executedAt = DateTime.Now;
            var cutoff = executedAt.AddDays(-30);
            return await SqliteWork.WriteAsync(_factory, async db =>
            {
                var eligibleQuery = db.Orders.Where(x =>
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

                db.OrderItems.RemoveRange(orders.SelectMany(x => x.Items));
                db.Customers.RemoveRange(orders.Select(x => x.Customer));
                db.Orders.RemoveRange(orders);

                return new OrderCleanupResult(
                    orders.Count,
                    orders.Count,
                    officialCount,
                    sandboxCount,
                    readyCount,
                    cancelledCount,
                    executedAt);
            });
        }
        finally
        {
            CleanupLock.Release();
        }
    }

    public async Task DeleteOrderAsync(Guid orderId)
    {
        var imageUrls = await SqliteWork.WriteAsync(_factory, async db =>
        {
            var order = await db.Orders
                .Include(x => x.Customer)
                .Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == orderId);

            if (order is null)
                return new List<string>();

            if (order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Ready)
            {
                throw new InvalidOperationException("يمكن حذف الطلبية فقط إذا كانت بحالة ملغاة أو جاهز.");
            }

            var imageUrls = order.Items
                .Select(x => x.ProductImageUrl)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            db.OrderItems.RemoveRange(order.Items);
            db.Customers.Remove(order.Customer);
            db.Orders.Remove(order);
            return imageUrls;
        });

        foreach (var imageUrl in imageUrls)
        {
            await DeleteImageIfUnusedAsync(imageUrl);
        }
    }

    private void EnsureAdmin(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_adminAuthService.IsTokenValid(token))
            throw new UnauthorizedAccessException("انتهت صلاحية جلسة الإدارة.");
    }

    private InventoryException Failure(InventoryFailureReason reason, string message, Guid correlation,
        Guid? productId, Guid? sizeId, long requested, int? matches = null, int? affected = null)
    {
        _logger.LogWarning("InventoryRejected Reason={Reason} Correlation={Correlation} ProductId={ProductId} ProductSizeId={ProductSizeId} Requested={Requested} Matches={Matches} Affected={Affected}",
            reason, correlation, productId, sizeId, requested, matches, affected);
        return new(reason, message);
    }

    private async Task<T> WriteInventoryAsync<T>(Func<AppDbContext, Task<T>> action, Guid correlation, List<CartItem>? items = null)
    {
        try { return await SqliteWork.WriteAsync(_factory, action); }
        catch (InventoryException ex) when (ex.Reason is InventoryFailureReason.ConcurrentChange or InventoryFailureReason.InvalidQuantity)
        {
            throw Failure(ex.Reason, ex.Message, correlation, items?.Count == 1 ? items[0].ProductId : null,
                items?.Count == 1 ? items[0].ProductSizeId : null, items?.Sum(i => (long)i.Quantity) ?? 0);
        }
    }

    private async Task DeleteImageIfUnusedAsync(string? imageUrl)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.StartsWith("/uploads/"))
            return;

        var usedByProduct = await db.Products.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByProductImage = await db.ProductImages.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByOrder = await db.OrderItems.AnyAsync(x => x.ProductImageUrl == imageUrl);

        if (usedByProduct || usedByProductImage || usedByOrder)
            return;

        var fileName = Path.GetFileName(imageUrl);
        var filePath = Path.Combine(_storagePaths.UploadsPath, fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static CustomerOrderSummary ToCustomerOrderSummary(Order order)
    {
        var hasDiscount = order.DiscountAmount > 0 || !string.IsNullOrWhiteSpace(order.DiscountName);
        return new CustomerOrderSummary
        {
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            CustomerName = order.Customer.FullName,
            CreatedAt = order.CreatedAt,
            Status = order.Status,
            FinalTotal = order.FinalTotal > 0 ? order.FinalTotal : order.TotalPrice,
            OriginalTotal = hasDiscount ? order.OriginalTotal : null,
            DiscountAmount = order.DiscountAmount > 0 ? order.DiscountAmount : null,
            DiscountName = string.IsNullOrWhiteSpace(order.DiscountName) ? null : order.DiscountName,
            City = order.Customer.City,
            Street = order.Customer.Street,
            HouseNumber = order.Customer.HouseNumber,
            IsSandbox = order.IsSandbox,
            TotalQuantity = order.Items.Sum(x => x.Quantity),
            Items = order.Items.Select(x => new CustomerOrderItemSummary
            {
                ProductName = x.ProductName,
                ProductImageUrl = x.ProductImageUrl,
                Size = x.Size,
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice,
                TotalPrice = x.TotalPrice
            }).ToList()
        };
    }

    private static CustomerOrderSummary ToHistoricalCustomerOrderSummary(SalesHistory history)
    {
        return new CustomerOrderSummary
        {
            OrderId = history.OriginalOrderId,
            OrderNumber = history.OrderNumber,
            CustomerName = history.CustomerName,
            CreatedAt = ReportsService.ToJerusalem(history.CreatedAtUtc),
            Status = history.LastStatus,
            FinalTotal = history.FinalTotal,
            IsHistorical = true,
            TotalQuantity = history.TotalQuantity
        };
    }

    private sealed record CustomerIdentityCandidate(string CustomerName, DateTime CreatedAt);
}
