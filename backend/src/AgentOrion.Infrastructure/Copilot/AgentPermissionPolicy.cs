namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentPermissionPolicy
{
    private static readonly HashSet<string> SensitiveToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_awb",
        "update_awb_status",
        "cancel_awb",
        "register_customer",
        "simulate_email"
    };

    public bool RequiresConfirmation(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && SensitiveToolNames.Contains(toolName);
}
