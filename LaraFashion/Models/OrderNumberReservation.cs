namespace LaraFashion.Models;

// Deliberately has no cascading FK: numbers remain reserved after order cleanup.
public class OrderNumberReservation
{
    public string Number { get; set; } = string.Empty;
    public Guid OrderId { get; set; }
}

public class CheckoutAttempt
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
}

public class OrderNumberAudit
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string PreviousNumber { get; set; } = string.Empty;
    public string NewNumber { get; set; } = string.Empty;
    public string AdminIdentity { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
}
