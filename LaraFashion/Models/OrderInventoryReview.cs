using LaraFashion.Models.Enums;

namespace LaraFashion.Models;

// Evidence of membership in a verified pre-identity database, never a size mapping.
// These records and the audit deliberately have no FK to live orders/products.
public class HistoricalOrderItemEvidence
{
    public Guid OrderItemId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public string ItemFingerprint { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string SourceMigration { get; set; } = "";
    public DateTime RegisteredAtUtc { get; set; }
}

public class OrderInventoryReview
{
    public Guid OperationId { get; set; }
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = "";
    public bool IsSandbox { get; set; }
    public OrderStatus PreviousStatus { get; set; }
    public OrderStatus NewStatus { get; set; }
    public long PreviousRevision { get; set; }
    public long NewRevision { get; set; }
    public Guid? PreviousOperationId { get; set; }
    public string AdminIdentity { get; set; } = "";
    public DateTime ExecutedAtUtc { get; set; }
    public string Reason { get; set; } = "";
    public int PreviewVersion { get; set; }
    public string RequestHash { get; set; } = "";
    public List<OrderInventoryReviewLine> Lines { get; set; } = [];
    public List<OrderInventoryReviewMovement> Movements { get; set; } = [];
}

public class OrderInventoryReviewLine
{
    public Guid OperationId { get; set; }
    public Guid OrderItemId { get; set; }
    public Guid ProductId { get; set; }
    public string HistoricalSize { get; set; } = "";
    public bool IsHistorical { get; set; }
    public Guid TargetSizeId { get; set; }
    public string TargetSizeName { get; set; } = "";
    public string TargetSizeKey { get; set; } = "";
    public int Quantity { get; set; }
    public int Direction { get; set; }
}

public class OrderInventoryReviewMovement
{
    public Guid OperationId { get; set; }
    public Guid TargetSizeId { get; set; }
    public Guid ProductId { get; set; }
    public string TargetSizeName { get; set; } = "";
    public int Quantity { get; set; }
    public int Direction { get; set; }
    public int StockBefore { get; set; }
    public int StockAfter { get; set; }
    public long TargetRevisionBefore { get; set; }
    public long TargetRevisionAfter { get; set; }
}
