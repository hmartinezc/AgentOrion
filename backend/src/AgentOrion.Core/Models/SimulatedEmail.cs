namespace AgentOrion.Core.Models;

public class SimulatedEmail
{
    public int Id { get; set; }
    public int? ShipmentId { get; set; }
    public string? RecipientEmail { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "simulated"; // simulated, failed
}
