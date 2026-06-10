namespace AgentOrion.Core.Models;

public class DatabaseHealthStatus
{
    public bool CanConnect { get; set; }
    public string Provider { get; set; } = "sqlite";
    public string DatabasePath { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string? JournalMode { get; set; }
    public bool ForeignKeysEnabled { get; set; }
    public int BusyTimeoutMs { get; set; }
    public string? Error { get; set; }
}
