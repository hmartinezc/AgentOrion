namespace AgentOrion.Core.Models;

public class AgentAuditLog
{
    public int Id { get; set; }
    public string? SessionId { get; set; }
    public string? UserPrompt { get; set; }
    public string? AgentResponse { get; set; }
    public string? ToolCalled { get; set; }
    public string? ToolInputJson { get; set; }
    public string? ToolOutputJson { get; set; }
    public bool WasOffTopic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
