using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LaraFashion.Services;

public partial class OrderService
{
    private sealed record ReviewTicket(int Version, Guid OperationId, Guid OrderId, string OrderNumber,
        bool IsSandbox, string AdminIdentity, OrderStatus PreviousStatus, OrderStatus NewStatus,
        long Revision, DateTime ExpiresAtUtc, Guid? PreviousOperationId,
        List<TicketItem> Items, List<OrderInventoryReviewMovement> Movements);
    private sealed record TicketItem(OrderInventoryReviewLine Line, string Fingerprint, Guid? OriginalSizeId);
    private static bool NeedsInventory(OrderStatus current, OrderStatus requested) =>
        current != requested && (current == OrderStatus.Cancelled || requested == OrderStatus.Cancelled);
    private static InventoryException ReviewConflict() => new(InventoryFailureReason.ConcurrentChange,
        "تغيرت الحالة أو المقاس أو المخزون منذ المعاينة. أعد المعاينة وراجع الأثر ثم أكد من جديد.");
    private static void ValidateInventoryOrder(Order order)
    {
        if (!Enum.IsDefined(order.Status) || order.InventoryRevision < 0 || order.InventoryRevision == long.MaxValue)
            throw new InvalidOperationException("حالة الطلب الحالية أو إصدارها غير صالح؛ يلزم فحص مستقل.");
        if (order.Items.Count == 0 || order.Items.Any(i => i.Id == Guid.Empty || i.OrderId != order.Id || i.ProductId == Guid.Empty || i.Quantity <= 0))
            throw new InventoryException(InventoryFailureReason.InvalidQuantity, "أسطر الطلب أو هوياتها أو كمياتها غير صالحة؛ لا يمكن تسويتها.");
    }

    public async Task<OrderInventoryReviewNeed> GetInventoryReviewAsync(Guid orderId, OrderStatus status, string adminToken)
    {
        EnsureAdmin(adminToken);
        await using var db = await _factory.CreateDbContextAsync();
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId)
            ?? throw new InvalidOperationException("الطلب لم يعد موجوداً.");
        return await BuildReviewNeedAsync(db, order, status);
    }

    private static bool IsAdministrativeCandidate(Product product, ProductSize size) =>
        size.Id != Guid.Empty && size.ProductId == product.Id && size.Quantity >= 0
        && size.ReviewRevision is >= 0 and < long.MaxValue
        && SizeNamePolicy.IsValid(size.SizeName) && size.NormalizedName == SizeNamePolicy.Key(size.SizeName)
        && product.Sizes.Count(s => SizeNamePolicy.Key(s.SizeName) == size.NormalizedName) == 1;

    private async Task<OrderInventoryReviewNeed> BuildReviewNeedAsync(AppDbContext db, Order order, OrderStatus status)
    {
        if (!Enum.IsDefined(status) || !NeedsInventory(order.Status, status))
            throw new InvalidOperationException("هذه الحالة لا تتطلب مراجعة مخزون.");
        ValidateInventoryOrder(order);
        var evidence = await db.HistoricalOrderItemEvidence.AsNoTracking().Where(e => e.OrderId == order.Id).ToListAsync();
        var previous = await db.OrderInventoryReviews.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.OrderId == order.Id).OrderByDescending(x => x.NewRevision).FirstOrDefaultAsync();
        var items = new List<InventoryReviewItem>();
        foreach (var item in order.Items.OrderBy(i => i.Id))
        {
            var product = await db.Products.AsNoTracking().Include(p => p.Sizes).SingleOrDefaultAsync(p => p.Id == item.ProductId);
            if (item.ProductSizeId.HasValue)
            {
                if (product is null) throw new InventoryException(InventoryFailureReason.ProductUnavailable, "منتج سطر ذي هوية محفوظة لم يعد موجوداً؛ لا يجوز تجاوز حمايته بالتسوية.");
                var selected = InventorySelection.Resolve(product, item.ProductSizeId, item.Size);
                if (!IsAdministrativeCandidate(product, selected))
                    throw new InventoryException(InventoryFailureReason.StaleIdentity, "بيانات المقاس ذي الهوية المحفوظة غير صالحة؛ لا يجوز تجاوز حمايته بالتسوية.");
                continue;
            }
            string? blocked = null;
            var proof = evidence.SingleOrDefault(e => e.OrderItemId == item.Id);
            if (proof is null || proof.ProductId != item.ProductId || proof.ItemFingerprint != HistoricalItemFingerprint.Create(item))
                blocked = "لا يوجد إثبات مطابق بأن هذا السطر سبق ترقية هوية المقاسات؛ NULL وحدها لا تجيز التسوية.";
            else if (!SizeNamePolicy.IsValid(item.Size)) blocked = "لقطة المقاس الأصلية غير صالحة؛ تحتاج قراراً منفصلاً.";
            else if (product is null) blocked = "المنتج الأصلي مفقود؛ لا يمكن اختيار منتج بديل أو تجاهل المخزون.";
            var candidates = product?.Sizes.Where(s => IsAdministrativeCandidate(product, s))
                .OrderBy(s => s.SizeName).ThenBy(s => s.Id).Select(s => new InventoryReviewCandidate(s.Id, s.SizeName, s.Quantity)).ToList() ?? [];
            var prior = previous?.Lines.SingleOrDefault(l => l.OrderItemId == item.Id && l.IsHistorical);
            if (blocked is null && candidates.Count == 0) blocked = "لا يوجد مقاس حالي صالح تابع للمنتج الأصلي.";
            if (blocked is null && prior is not null)
            {
                var priorSize = product!.Sizes.SingleOrDefault(s => s.Id == prior.TargetSizeId);
                if (priorSize is null || !IsAdministrativeCandidate(product, priorSize)
                    || SizeNamePolicy.Key(priorSize.SizeName) != prior.TargetSizeKey)
                    blocked = "مقصد التسوية السابقة حُذف أو تغيرت دلالته؛ تصحيح التسوية أو نقل المخزون يحتاج قراراً منفصلاً.";
                else candidates = candidates.Where(c => c.Id == prior.TargetSizeId).ToList();
            }
            items.Add(new(item.Id, item.ProductId, item.ProductName, item.Size, item.Quantity, prior?.TargetSizeId, candidates, blocked));
        }
        if (items.Count == 0) throw new InvalidOperationException("الطلب لا يحتوي أسطراً تاريخية تحتاج مراجعة.");
        return new(order.Id, order.OrderNumber, order.Status, status, order.InventoryRevision, items);
    }

    public async Task<InventoryReviewPreview> PreviewInventoryReviewAsync(Guid orderId, OrderStatus status,
        IReadOnlyDictionary<Guid, Guid> selections, string adminToken)
    {
        EnsureAdmin(adminToken);
        var admin = _adminAuthService.GetAdminIdentity(adminToken)!;
        var choices = selections.ToDictionary(x => x.Key, x => x.Value);
        await using var db = await _factory.CreateDbContextAsync();
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == orderId)
            ?? throw new InvalidOperationException("الطلب لم يعد موجوداً.");
        var ticket = await BuildTicketAsync(db, order, status, choices, admin, Guid.NewGuid());
        var protector = _reviewProtector ?? throw new InvalidOperationException("حماية معاينة التسوية غير متاحة.");
        return new(ticket.OperationId, orderId, protector.Protect(JsonSerializer.Serialize(ticket)), order.Status,
            status, order.InventoryRevision, ticket.Movements, ticket.Items.Select(x => x.Line).ToList());
    }

    private async Task<ReviewTicket> BuildTicketAsync(AppDbContext db, Order order, OrderStatus status,
        IReadOnlyDictionary<Guid, Guid> choices, string admin, Guid operationId)
    {
        var need = await BuildReviewNeedAsync(db, order, status);
        if (need.Items.Any(i => i.BlockedReason is not null))
            throw new InvalidOperationException(string.Join(" ", need.Items.Where(i => i.BlockedReason is not null).Select(i => i.BlockedReason).Distinct()));
        if (choices.Count != need.Items.Count || need.Items.Any(i => !choices.ContainsKey(i.OrderItemId)))
            throw new InvalidOperationException("اختر مقصد كل سطر صراحة، دون أسطر إضافية أو مكررة.");
        foreach (var item in need.Items)
            if (!item.Candidates.Any(c => c.Id == choices[item.OrderItemId]))
                throw new InvalidOperationException("المقصد المختار غير صالح لهذا السطر أو يختلف عن مقصد التسوية السابقة.");
        var direction = status == OrderStatus.Cancelled ? 1 : -1;
        var lines = new List<TicketItem>();
        foreach (var item in order.Items.OrderBy(i => i.Id))
        {
            var targetId = item.ProductSizeId ?? choices[item.Id];
            var target = await db.ProductSizes.AsNoTracking().SingleAsync(s => s.Id == targetId && s.ProductId == item.ProductId);
            lines.Add(new(new() { OperationId = operationId, OrderItemId = item.Id, ProductId = item.ProductId,
                HistoricalSize = item.Size, IsHistorical = item.ProductSizeId is null, TargetSizeId = targetId,
                TargetSizeName = target.SizeName, TargetSizeKey = target.NormalizedName, Quantity = item.Quantity, Direction = direction },
                HistoricalItemFingerprint.Create(item), item.ProductSizeId));
        }
        var movements = new List<OrderInventoryReviewMovement>();
        foreach (var group in lines.GroupBy(x => x.Line.TargetSizeId).OrderBy(g => g.Key))
        {
            var quantity = InventorySelection.TotalQuantity(group.Select(x => x.Line.Quantity));
            var line = group.First().Line;
            var target = await db.ProductSizes.AsNoTracking().SingleAsync(s => s.Id == group.Key);
            var after = (long)target.Quantity + direction * (long)quantity;
            if (after is < 0 or > int.MaxValue)
                throw new InventoryException(InventoryFailureReason.InsufficientStock, "المجموع غير متاح أو يتجاوز حد المخزون. لم تتغير الحالة أو الكميات.");
            movements.Add(new() { OperationId = operationId, TargetSizeId = target.Id, ProductId = target.ProductId,
                TargetSizeName = line.TargetSizeName, Quantity = quantity, Direction = direction,
                StockBefore = target.Quantity, StockAfter = (int)after, TargetRevisionBefore = target.ReviewRevision,
                TargetRevisionAfter = checked(target.ReviewRevision + 1) });
        }
        var previousId = await db.OrderInventoryReviews.Where(x => x.OrderId == order.Id)
            .OrderByDescending(x => x.NewRevision).Select(x => (Guid?)x.OperationId).FirstOrDefaultAsync();
        return new(1, operationId, order.Id, order.OrderNumber, order.IsSandbox, admin, order.Status, status,
            order.InventoryRevision, DateTime.UtcNow.AddMinutes(15), previousId, lines, movements);
    }

    public Task<InventoryReviewResult> ConfirmInventoryReviewAsync(ConfirmInventoryReview input, string adminToken)
    {
        EnsureAdmin(adminToken);
        var admin = _adminAuthService.GetAdminIdentity(adminToken)!;
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length is < 1 or > 500) throw new InvalidOperationException("اكتب سبب المراجعة من 1 إلى 500 محرف.");
        if (input.OperationId == Guid.Empty || input.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(input.ProtectedTicket)
            || input.ProtectedTicket.Length > 262144) throw new InvalidOperationException("معاينة التسوية غير صالحة.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { input.OperationId, input.OrderId, input.ProtectedTicket, Reason = reason, AdminIdentity = admin }))));
        return SqliteWork.WriteAsync(_factory, async db =>
        {
            // Durable replay is checked before decrypting an old/expired ticket, including after restart/cleanup.
            EnsureAdmin(adminToken);
            var saved = await db.OrderInventoryReviews.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == input.OperationId);
            if (saved is not null)
            {
                if (saved.OrderId != input.OrderId || saved.AdminIdentity != admin || saved.RequestHash != hash)
                    throw new InvalidOperationException("معرف العملية محفوظ بمحتوى مختلف؛ لن تُكرر الحركة.");
                return new InventoryReviewResult(saved.OperationId, saved.OrderId, saved.NewStatus, saved.NewRevision, true);
            }
            ReviewTicket ticket;
            try { ticket = JsonSerializer.Deserialize<ReviewTicket>(_reviewProtector!.Unprotect(input.ProtectedTicket))!; }
            catch (Exception ex) when (ex is CryptographicException or JsonException or NullReferenceException)
            { throw new InvalidOperationException("تعذر التحقق من المعاينة. أعد المعاينة والتأكيد."); }
            if (ticket is null || ticket.Version != 1 || ticket.OperationId != input.OperationId || ticket.OrderId != input.OrderId
                || ticket.AdminIdentity != admin || ticket.ExpiresAtUtc < DateTime.UtcNow) throw ReviewConflict();
            var order = await db.Orders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == ticket.OrderId);
            if (order is null || order.Status != ticket.PreviousStatus || order.InventoryRevision != ticket.Revision
                || order.IsSandbox != ticket.IsSandbox) throw ReviewConflict();
            var choices = ticket.Items.Where(i => i.Line.IsHistorical).ToDictionary(i => i.Line.OrderItemId, i => i.Line.TargetSizeId);
            var current = await BuildTicketAsync(db, order, ticket.NewStatus, choices, admin, ticket.OperationId);
            if (current.PreviousOperationId != ticket.PreviousOperationId
                || JsonSerializer.Serialize(current.Items) != JsonSerializer.Serialize(ticket.Items)
                || JsonSerializer.Serialize(current.Movements) != JsonSerializer.Serialize(ticket.Movements)) throw ReviewConflict();
            foreach (var movement in current.Movements)
            {
                var changed = await db.ProductSizes.Where(s => s.Id == movement.TargetSizeId && s.ProductId == movement.ProductId
                    && s.Quantity == movement.StockBefore && s.SizeName == movement.TargetSizeName && s.ReviewRevision == movement.TargetRevisionBefore)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, movement.StockAfter));
                if (changed != 1) throw ReviewConflict();
            }
            var now = DateTime.UtcNow;
            var review = new OrderInventoryReview { OperationId = ticket.OperationId, OrderId = order.Id,
                OrderNumber = order.OrderNumber, IsSandbox = order.IsSandbox, PreviousStatus = order.Status,
                NewStatus = ticket.NewStatus, PreviousRevision = order.InventoryRevision,
                NewRevision = checked(order.InventoryRevision + 1), PreviousOperationId = ticket.PreviousOperationId,
                AdminIdentity = admin, ExecutedAtUtc = now, Reason = reason, PreviewVersion = ticket.Version,
                RequestHash = hash, Lines = current.Items.Select(i => i.Line).ToList(), Movements = current.Movements };
            db.OrderInventoryReviews.Add(review);
            order.Status = ticket.NewStatus; order.InventoryRevision = review.NewRevision; order.UpdatedAt = now.ToLocalTime();
            if (!order.IsSandbox)
                await db.SalesHistory.Where(h => h.OriginalOrderId == order.Id).ExecuteUpdateAsync(s =>
                    s.SetProperty(h => h.LastStatus, order.Status).SetProperty(h => h.StatusUpdatedAtUtc, now));
            return new InventoryReviewResult(ticket.OperationId, order.Id, order.Status, order.InventoryRevision, false);
        });
    }

    public async Task<List<OrderInventoryReview>> GetInventoryReviewAuditAsync(Guid orderId, string adminToken)
    {
        EnsureAdmin(adminToken);
        await using var db = await _factory.CreateDbContextAsync();
        return await db.OrderInventoryReviews.AsNoTracking().Include(x => x.Lines).Include(x => x.Movements)
            .Where(x => x.OrderId == orderId).OrderBy(x => x.NewRevision).ToListAsync();
    }
}
