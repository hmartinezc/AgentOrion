using Microsoft.Extensions.AI;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentRequestRouter
{
    private readonly AgentCatalog _catalog;
    private readonly IReadOnlyDictionary<string, AgentRouteDefinition> _routesByName;
    private readonly IReadOnlyList<AgentRouteDefinition> _specialistRoutes;

    public AgentRequestRouter(AgentCatalogProvider catalogProvider)
    {
        _catalog = catalogProvider.Catalog;
        _routesByName = _catalog.Routes.ToDictionary(route => route.Name, StringComparer.OrdinalIgnoreCase);
        _specialistRoutes = _catalog.Routes
            .Where(route => route.IncludeInSpecialistMatching)
            .ToArray();
    }

    public AgentSessionProfile SelectProfile(
        string prompt,
        IReadOnlyDictionary<string, AIFunction> toolCatalog,
        string? fallbackRouteName = null)
    {
        var normalizedPrompt = prompt.ToLowerInvariant();
        var matches = _specialistRoutes
            .Where(route => route.Keywords.Any(keyword => normalizedPrompt.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matches.Count switch
        {
            0 when !string.IsNullOrWhiteSpace(fallbackRouteName) && _routesByName.TryGetValue(fallbackRouteName, out var fallbackRoute)
                => BuildProfile(fallbackRoute, toolCatalog),
            0 => BuildProfile(_routesByName[_catalog.DefaultRoute], toolCatalog),
            1 => BuildProfile(matches[0], toolCatalog),
            _ => BuildMixedProfile(matches, toolCatalog)
        };
    }

    private AgentSessionProfile BuildMixedProfile(IReadOnlyList<AgentRouteDefinition> routes, IReadOnlyDictionary<string, AIFunction> toolCatalog)
    {
        var skillNames = routes
            .SelectMany(route => route.SkillNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var toolNames = routes
            .SelectMany(route => route.ToolNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AgentSessionProfile
        {
            RouteName = _catalog.MixedRoute.Name,
            DisplayName = _catalog.MixedRoute.DisplayName,
            SpecialistPrompt = _catalog.MixedRoute.SpecialistPrompt,
            SkillNames = skillNames,
            Tools = ResolveTools(toolNames, toolCatalog)
        };
    }

    private static AgentSessionProfile BuildProfile(AgentRouteDefinition route, IReadOnlyDictionary<string, AIFunction> toolCatalog)
    {
        return new AgentSessionProfile
        {
            RouteName = route.Name,
            DisplayName = route.DisplayName,
            SpecialistPrompt = route.SpecialistPrompt,
            SkillNames = route.SkillNames,
            Tools = ResolveTools(route.ToolNames, toolCatalog)
        };
    }

    private static IReadOnlyList<AIFunction> ResolveTools(IEnumerable<string> toolNames, IReadOnlyDictionary<string, AIFunction> toolCatalog)
    {
        var resolved = new List<AIFunction>();

        foreach (var toolName in toolNames)
        {
            if (toolCatalog.TryGetValue(toolName, out var tool))
            {
                resolved.Add(tool);
            }
        }

        return resolved;
    }
}
