using LaraFashion.Models.Enums;

namespace LaraFashion.Models;

public sealed record CustomerOrderIdentityResult(
    bool HasOrders,
    string CustomerName,
    string? NormalizedPhone);

public sealed record CustomerOrdersResult(
    string CustomerName,
    string NormalizedPhone,
    IReadOnlyList<CustomerOrderSummary> Orders);

public sealed class MyOrdersCacheEntry
{
    public string Mode { get; init; } = string.Empty;
    public bool Verified { get; init; }
    public string NormalizedPhone { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public IReadOnlyList<CustomerOrderSummary> Orders { get; init; } = [];
    public DateTime LastSyncedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class CustomerOrderSummary
{
    public Guid? OrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public OrderStatus Status { get; init; }
    public decimal FinalTotal { get; init; }
    public decimal? OriginalTotal { get; init; }
    public decimal? DiscountAmount { get; init; }
    public string? DiscountName { get; init; }
    public string? City { get; init; }
    public string? Street { get; init; }
    public string? HouseNumber { get; init; }
    public bool IsSandbox { get; init; }
    public bool IsHistorical { get; init; }
    public int TotalQuantity { get; init; }
    public IReadOnlyList<CustomerOrderItemSummary> Items { get; init; } = [];
}

public sealed class CustomerOrderItemSummary
{
    public string ProductName { get; init; } = string.Empty;
    public string ProductImageUrl { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal TotalPrice { get; init; }
}
