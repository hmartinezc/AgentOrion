namespace AgentOrion.Core.Configuration;

public class AgentOrionOptions
{
    public string DbPath { get; set; } = "data/agentorion.db";
    public string AgentCatalogPath { get; set; } = "agent-catalog.json";
    public List<string> SkillDirectories { get; set; } = new();
    public CopilotOptions Copilot { get; set; } = new();
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
