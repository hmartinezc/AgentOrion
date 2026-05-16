namespace AgentOrion.Infrastructure.Copilot;

public static class ChatModes
{
    public const string Fast = "fast";
    public const string Memory = "memory";

    public static string Normalize(string? mode)
    {
        return string.Equals(mode, Fast, StringComparison.OrdinalIgnoreCase)
            ? Fast
            : Memory;
    }
}
