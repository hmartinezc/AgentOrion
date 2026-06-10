namespace AgentOrion.Core.Configuration;

public class AgentOrionOptions
{
    public string DbPath { get; set; } = "data/agentorion.db";
    public string AgentCatalogPath { get; set; } = "agent-catalog.json";
    public List<string> SkillDirectories { get; set; } = new();
    public CopilotOptions Copilot { get; set; } = new();
    public OperationsOptions Operations { get; set; } = new();
}

public class CopilotOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public ProviderOptions Provider { get; set; } = new();
}

public class ProviderOptions
{
    public string Type { get; set; } = "openai";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string? ApiKey { get; set; }
    public string WireApi { get; set; } = "completions";
}

public class OperationsOptions
{
    public AwbApiOptions AwbApi { get; set; } = new();
}

public class AwbApiOptions
{
    public string Mode { get; set; } = "fake";
    public string? BaseUrl { get; set; }
    public string AuthMode { get; set; } = "none";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string CreatePath { get; set; } = "/api/awb/reservations";
    public string GetPath { get; set; } = "/api/awb/reservations/{awbNumber}";
    public string UpdateStatusPath { get; set; } = "/api/awb/reservations/{awbNumber}/status";
    public string CancelPath { get; set; } = "/api/awb/reservations/{awbNumber}/cancel";
}
