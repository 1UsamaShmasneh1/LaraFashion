using LaraFashion.Models;
using LaraFashion.Models.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LaraFashion.Services;

public sealed record InventoryReviewCandidate(Guid Id, string Name, int Quantity);
public sealed record InventoryReviewItem(Guid OrderItemId, Guid ProductId, string ProductName,
    string HistoricalSize, int Quantity, Guid? PreviousTargetId,
    IReadOnlyList<InventoryReviewCandidate> Candidates, string? BlockedReason);
public sealed record OrderInventoryReviewNeed(Guid OrderId, string OrderNumber, OrderStatus CurrentStatus,
    OrderStatus RequestedStatus, long Revision, IReadOnlyList<InventoryReviewItem> Items);
public sealed class InventoryReviewRequiredException(IReadOnlyList<OrderInventoryReviewNeed> orders)
    : InventoryException(InventoryFailureReason.HistoricalIdentityUnknown,
        "تحتاج هذه العملية إلى مراجعة مخزون الطلب القديم. لم تتغير الحالات أو الكميات.")
{
    public IReadOnlyList<OrderInventoryReviewNeed> Orders { get; } = orders;
}
public sealed record InventoryReviewPreview(Guid OperationId, Guid OrderId, string ProtectedTicket,
    OrderStatus CurrentStatus, OrderStatus RequestedStatus, long Revision,
    IReadOnlyList<OrderInventoryReviewMovement> Movements, IReadOnlyList<OrderInventoryReviewLine> Lines);
public sealed record ConfirmInventoryReview(Guid OperationId, Guid OrderId, string ProtectedTicket, string Reason);
public sealed record InventoryReviewResult(Guid OperationId, Guid OrderId, OrderStatus AppliedStatus, long AppliedRevision, bool Replayed);

public static class HistoricalItemFingerprint
{
    // Versioned, invariant representation of original purchase data; no customer fields.
    public static string Create(OrderItem item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Version = 1, item.Id, item.OrderId, item.ProductId, item.ProductName,
            item.ProductSerialNumber, item.ProductImageUrl, item.Size, item.Quantity, item.UnitPrice }))));
}
