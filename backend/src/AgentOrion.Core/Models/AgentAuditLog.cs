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
    public string? RouteName { get; set; }
    public string? RouteDisplayName { get; set; }
    public string? Model { get; set; }
    public string? ChatMode { get; set; }
    public string? ToolsJson { get; set; }
    public string? SkillsJson { get; set; }
    public string? Error { get; set; }
    public string? UsageJson { get; set; }
    public string? RoutingTraceJson { get; set; }
    public double? RouteConfidence { get; set; }
    public string? RoutingReason { get; set; }
    public double? DurationMs { get; set; }
    public bool WasOffTopic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
