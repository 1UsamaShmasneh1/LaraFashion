using LaraFashion.Models.Enums;

namespace LaraFashion.Models;

public class Order
{
    public Guid Id { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public CustomerInfo Customer { get; set; } = new();

    public List<OrderItem> Items { get; set; } = new();

    public OrderStatus Status { get; set; } = OrderStatus.New;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public decimal TotalPrice => Items.Sum(x => x.TotalPrice);

    public decimal OriginalTotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public string DiscountName { get; set; } = string.Empty;

    public decimal FinalTotal { get; set; }
}