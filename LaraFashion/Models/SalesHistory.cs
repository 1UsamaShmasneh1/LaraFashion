using LaraFashion.Models.Enums;

namespace LaraFashion.Models;

public class SalesHistory
{
    public Guid Id { get; set; }
    public Guid? OriginalOrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public int TotalQuantity { get; set; }
    public decimal FinalTotal { get; set; }
    public OrderStatus LastStatus { get; set; }
    public DateTime StatusUpdatedAtUtc { get; set; }
}
