using System.Text.Json;
using AgentOrion.Core.Configuration;
using Microsoft.Extensions.Options;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentCatalog
{
    public string DefaultRoute { get; init; } = "operations-general";
    public MixedRouteDefinition MixedRoute { get; init; } = new();
    public IReadOnlyList<AgentRouteDefinition> Routes { get; init; } = Array.Empty<AgentRouteDefinition>();
}

public sealed class MixedRouteDefinition
{
    public string Name { get; init; } = "mixed-operations";
    public string DisplayName { get; init; } = "Operacion Mixta";
    public string SpecialistPrompt { get; init; } = string.Empty;
}

public sealed class AgentRouteDefinition
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string SpecialistPrompt { get; init; }
    public IReadOnlyList<string> SkillNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ToolNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public bool IncludeInSpecialistMatching { get; init; } = true;
    public string? Model { get; init; }
    public RouteProviderDefinition? Provider { get; init; }
}

public sealed class RouteProviderDefinition
{
    public string? Type { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? WireApi { get; init; }
}

public sealed class AgentCatalogProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AgentCatalog Catalog { get; }

    public AgentCatalogProvider(IOptions<AgentOrionOptions> options, string contentRootPath)
    {
        var configuredPath = options.Value.AgentCatalogPath;
        var catalogPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));

        Catalog = Load(catalogPath);
    }

    public static AgentCatalog Load(string catalogPath)
    {
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException($"Agent catalog not found: {catalogPath}", catalogPath);
        }

        var catalog = JsonSerializer.Deserialize<AgentCatalog>(File.ReadAllText(catalogPath), JsonOptions)
            ?? throw new InvalidOperationException($"Agent catalog is empty: {catalogPath}");

        Validate(catalog, catalogPath);
        return catalog;
    }

    private static void Validate(AgentCatalog catalog, string catalogPath)
    {
        if (string.IsNullOrWhiteSpace(catalog.DefaultRoute))
        {
            throw new InvalidOperationException($"Agent catalog default route is empty: {catalogPath}");
        }

        ValidateMixedRoute(catalog.MixedRoute, catalogPath);

        if (catalog.Routes.Count == 0)
        {
            throw new InvalidOperationException($"Agent catalog has no routes: {catalogPath}");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in catalog.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.Name))
            {
                throw new InvalidOperationException($"Agent catalog contains a route without name: {catalogPath}");
            }

            if (string.IsNullOrWhiteSpace(route.DisplayName))
            {
                throw new InvalidOperationException($"Agent catalog route '{route.Name}' has an empty display name: {catalogPath}");
            }

            if (string.IsNullOrWhiteSpace(route.SpecialistPrompt))
            {
                throw new InvalidOperationException($"Agent catalog route '{route.Name}' has an empty specialist prompt: {catalogPath}");
            }

            if (!names.Add(route.Name))
            {
                throw new InvalidOperationException($"Agent catalog contains duplicate route '{route.Name}': {catalogPath}");
            }
        }

        if (!names.Contains(catalog.DefaultRoute))
        {
            throw new InvalidOperationException($"Agent catalog default route '{catalog.DefaultRoute}' does not exist: {catalogPath}");
        }
    }

    private static void ValidateMixedRoute(MixedRouteDefinition mixedRoute, string catalogPath)
    {
        if (string.IsNullOrWhiteSpace(mixedRoute.Name))
        {
            throw new InvalidOperationException($"Agent catalog mixed route has an empty name: {catalogPath}");
        }

        if (string.IsNullOrWhiteSpace(mixedRoute.DisplayName))
        {
            throw new InvalidOperationException($"Agent catalog mixed route has an empty display name: {catalogPath}");
        }

        if (string.IsNullOrWhiteSpace(mixedRoute.SpecialistPrompt))
        {
            throw new InvalidOperationException($"Agent catalog mixed route has an empty specialist prompt: {catalogPath}");
        }
    }
}
