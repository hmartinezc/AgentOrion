namespace AgentOrion.Core.Models;

public class ShipmentEvent
{
    public int Id { get; set; }
    public int ShipmentId { get; set; }
    public string? EventType { get; set; }
    public string? EventData { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
