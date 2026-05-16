namespace AgentOrion.Core.Models;

public class ConversationMemoryState
{
    public string SessionId { get; set; } = string.Empty;
    public string? LastRouteName { get; set; }
    public string? CurrentIntent { get; set; }
    public ConversationCustomerMemory Customer { get; set; } = new();
    public ConversationShipmentMemory Shipment { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ConversationCustomerMemory
{
    public int? CustomerId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? CompanyName { get; set; }
    public string? Country { get; set; }
    public string? Address { get; set; }
    public string? DocumentNumber { get; set; }
}

public class ConversationShipmentMemory
{
    public string? AwbNumber { get; set; }
    public string? ProductType { get; set; }
    public string? ProductName { get; set; }
    public double? QuantityKg { get; set; }
    public string? OriginAirport { get; set; }
    public string? DestinationAirport { get; set; }
    public DateTime? FlightDate { get; set; }
    public double? TemperatureRequiredC { get; set; }
    public string? Status { get; set; }
}
