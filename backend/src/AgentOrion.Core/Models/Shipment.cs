namespace AgentOrion.Core.Models;

public class Shipment
{
    public int Id { get; set; }
    public string? AwbNumber { get; set; }
    public int? CustomerId { get; set; }
    public string ProductType { get; set; } = string.Empty; // flores, pescado, frutas, mariscos
    public string? ProductName { get; set; }
    public double? QuantityKg { get; set; }
    public double? TemperatureRequiredC { get; set; }
    public string? OriginAirport { get; set; }
    public string? DestinationAirport { get; set; }
    public DateTime? FlightDate { get; set; }
    public string Status { get; set; } = "solicitado"; // solicitado, confirmado, en_transito, entregado, rechazado
    public string? PhytosanitaryCert { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
