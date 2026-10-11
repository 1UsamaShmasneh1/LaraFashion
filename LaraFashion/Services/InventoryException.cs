namespace LaraFashion.Services;

public enum InventoryFailureReason
{
    ProductUnavailable, SizeMissing, SizeChanged, InsufficientStock, AmbiguousSize,
    StaleIdentity, ConcurrentChange, InvalidQuantity, HistoricalIdentityUnknown
}

public class InventoryException : InvalidOperationException
{
    public InventoryFailureReason Reason { get; }
    public InventoryException(InventoryFailureReason reason, string message) : base(message) => Reason = reason;
}
