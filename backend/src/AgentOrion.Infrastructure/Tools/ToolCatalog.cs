using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Copilot;
using Microsoft.Extensions.AI;

namespace AgentOrion.Infrastructure.Tools;

public sealed class ToolCatalog
{
    public static readonly IReadOnlySet<string> KnownToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create_awb",
        "get_awb_status",
        "update_awb_status",
        "cancel_awb",
        "get_temperature_requirements",
        "register_customer",
        "search_customer",
        "simulate_email"
    };

    private readonly IAwbReservationGateway _awbReservations;
    private readonly ICustomerRepository _customers;
    private readonly ISimulatedEmailRepository _emails;
    private readonly IShipmentRepository _shipments;

    public ToolCatalog(
        IAwbReservationGateway awbReservations,
        ICustomerRepository customers,
        ISimulatedEmailRepository emails,
        IShipmentRepository shipments)
    {
        _awbReservations = awbReservations;
        _customers = customers;
        _emails = emails;
        _shipments = shipments;
    }

    public IReadOnlyList<AIFunction> CreateAllTools()
    {
        var tools = new AIFunction[]
        {
            AwbTools.CreateCreateAwbTool(_awbReservations),
            AwbTools.CreateGetAwbStatusTool(_awbReservations),
            AwbTools.CreateUpdateAwbStatusTool(_awbReservations),
            AwbTools.CreateCancelAwbTool(_awbReservations),
            AwbTools.CreateGetTemperatureRequirementsTool(),
            CustomerTools.CreateRegisterCustomerTool(_customers),
            CustomerTools.CreateSearchCustomerTool(_customers),
            NotificationTools.CreateSimulateEmailTool(_emails, _shipments)
        };

        ValidateNoDuplicateToolNames(tools);
        return tools;
    }

    public static void ValidateCatalog(AgentCatalog catalog)
    {
        var missing = catalog.Routes
            .SelectMany(route => route.ToolNames.Select(toolName => new { route.Name, ToolName = toolName }))
            .Where(item => !KnownToolNames.Contains(item.ToolName))
            .Select(item => $"{item.Name}:{item.ToolName}")
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Agent catalog references unknown tools: {string.Join(", ", missing)}");
        }
    }

    private static void ValidateNoDuplicateToolNames(IEnumerable<AIFunction> tools)
    {
        var duplicates = tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"Duplicate tool registrations: {string.Join(", ", duplicates)}");
        }
    }
}
