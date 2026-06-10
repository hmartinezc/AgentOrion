using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentRequestRouter
{
    private const int MinimumRouteScore = 3;
    private const int MinimumMixedRouteScore = 4;
    private const int MixedRouteScoreGap = 2;
    private const double MiniRouterConfidenceThreshold = 0.55;
    private static readonly Regex TokenRegex = new(@"\p{L}+|\d+", RegexOptions.Compiled);
    private static readonly HashSet<string> WeakProductKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "flores",
        "pescado",
        "mariscos",
        "frutas",
        "fruta",
        "perecedero"
    };

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
        string? fallbackRouteName = null) =>
        SelectProfileWithTrace(prompt, toolCatalog, fallbackRouteName).Profile;

    public AgentRoutingDecision SelectProfileWithTrace(
        string prompt,
        IReadOnlyDictionary<string, AIFunction> toolCatalog,
        string? fallbackRouteName = null)
    {
        var normalizedPrompt = Normalize(prompt);
        var promptTokens = Tokenize(normalizedPrompt);
        var routeMatches = _specialistRoutes
            .Select(route => ScoreRoute(route, normalizedPrompt, promptTokens))
            .OrderByDescending(match => match.Score)
            .ToList();
        var eligibleRoutes = routeMatches
            .Where(match => match.Score >= MinimumRouteScore)
            .ToList();

        if (eligibleRoutes.Count == 0)
        {
            var selectedRoute = !string.IsNullOrWhiteSpace(fallbackRouteName) && _routesByName.TryGetValue(fallbackRouteName, out var fallbackRoute)
                ? fallbackRoute
                : _routesByName[_catalog.DefaultRoute];
            var fallbackReason = selectedRoute.Name == fallbackRouteName
                ? "No hubo senales suficientes en el prompt; se reutiliza la ultima ruta estable de la sesion."
                : "No hubo senales suficientes en el prompt; se usa la ruta general de triage.";

            var fallbackTrace = BuildTrace(
                selectedRoute.Name,
                selectedRoute.DisplayName,
                confidence: selectedRoute.Name == fallbackRouteName ? 0.45 : 0.3,
                fallbackReason,
                routeMatches,
                selectedRouteNames: BuildSelectedRouteSet(selectedRoute.Name),
                fallbackRouteName);

            return new AgentRoutingDecision(BuildProfile(selectedRoute, toolCatalog), fallbackTrace);
        }

        var topScore = eligibleRoutes[0].Score;
        var mixedMatches = eligibleRoutes
            .Where(match => match.Score >= MinimumMixedRouteScore && topScore - match.Score <= MixedRouteScoreGap)
            .ToList();

        if (mixedMatches.Count > 1)
        {
            var selectedRoutes = mixedMatches.Select(match => match.Route).ToArray();
            var mixedProfile = BuildMixedProfile(selectedRoutes, toolCatalog);
            var mixedTrace = BuildTrace(
                _catalog.MixedRoute.Name,
                _catalog.MixedRoute.DisplayName,
                CalculateConfidence(topScore, mixedMatches.Skip(1).FirstOrDefault()?.Score, isMixed: true),
                "Varias rutas tienen senales fuertes y cercanas; se activa coordinacion mixta.",
                routeMatches,
                selectedRoutes.Select(route => route.Name).Append(_catalog.MixedRoute.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                fallbackRouteName);

            return new AgentRoutingDecision(mixedProfile, mixedTrace);
        }

        var selected = eligibleRoutes[0];
        var secondScore = eligibleRoutes.Skip(1).FirstOrDefault()?.Score;
        var trace = BuildTrace(
            selected.Route.Name,
            selected.Route.DisplayName,
            CalculateConfidence(selected.Score, secondScore, isMixed: false),
            $"La ruta {selected.Route.DisplayName} tuvo el score mas alto por senales de intencion.",
            routeMatches,
            selectedRouteNames: BuildSelectedRouteSet(selected.Route.Name),
            fallbackRouteName);

        return new AgentRoutingDecision(BuildProfile(selected.Route, toolCatalog), trace);
    }

    private static RouteMatch ScoreRoute(AgentRouteDefinition route, string normalizedPrompt, IReadOnlySet<string> promptTokens)
    {
        var score = 0;
        var signals = new List<string>();

        foreach (var keyword in route.Keywords)
        {
            var normalizedKeyword = Normalize(keyword);
            if (string.IsNullOrWhiteSpace(normalizedKeyword))
            {
                continue;
            }

            if (normalizedKeyword.Contains(' '))
            {
                if (normalizedPrompt.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                    signals.Add($"keyword_phrase:{normalizedKeyword}");
                }

                continue;
            }

            if (promptTokens.Contains(normalizedKeyword))
            {
                var weight = WeakProductKeywords.Contains(normalizedKeyword) ? 1 : 2;
                score += weight;
                signals.Add(weight == 1 ? $"weak_product:{normalizedKeyword}" : $"keyword:{normalizedKeyword}");
            }
        }

        score += route.Name switch
        {
            "awb-dispatch" => ScoreAwbIntent(normalizedPrompt, promptTokens, signals),
            "cold-chain" => ScoreColdChainIntent(normalizedPrompt, promptTokens, signals),
            "client-comm" => ScoreClientCommunicationIntent(normalizedPrompt, promptTokens, signals),
            _ => 0
        };

        return new RouteMatch(route, score, signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static int ScoreAwbIntent(string normalizedPrompt, IReadOnlySet<string> promptTokens, List<string> signals)
    {
        var score = 0;
        if (ContainsAny(normalizedPrompt, "reserva", "booking", "despacho", "air waybill", "guia aerea") ||
            ContainsAny(promptTokens, "awb", "embarque", "envio"))
        {
            score += 5;
            signals.Add("intent:awb_reservation_or_lookup");
        }

        if (ContainsAny(promptTokens, "origen", "destino", "ruta", "vuelo"))
        {
            score += 2;
            signals.Add("intent:route_fields");
        }

        return score;
    }

    private static int ScoreColdChainIntent(string normalizedPrompt, IReadOnlySet<string> promptTokens, List<string> signals)
    {
        var score = 0;
        if (ContainsAny(normalizedPrompt, "cadena de frio", "cold chain") ||
            ContainsAny(promptTokens, "temperatura", "reefer", "refriger", "congel", "humedad", "etileno"))
        {
            score += 5;
            signals.Add("intent:cold_chain");
        }

        if (WeakProductKeywords.Any(promptTokens.Contains))
        {
            score += 1;
            signals.Add("context:perishable_product");
        }

        return score;
    }

    private static int ScoreClientCommunicationIntent(string normalizedPrompt, IReadOnlySet<string> promptTokens, List<string> signals)
    {
        var score = 0;
        if (ContainsAny(promptTokens, "correo", "email", "mail", "notificar", "notificacion", "mensaje", "comunic"))
        {
            score += 5;
            signals.Add("intent:client_communication");
        }

        var isCustomerRegistration =
            ContainsAny(normalizedPrompt, "registrar cliente", "registrar empresa", "crear cliente", "crear empresa", "nuevo cliente", "nuevo customer") ||
            (ContainsAny(promptTokens, "registrar", "crear", "nuevo") && ContainsAny(promptTokens, "cliente", "empresa", "customer", "company"));

        if (isCustomerRegistration)
        {
            score += 4;
            signals.Add("intent:customer_registration");
        }

        return score;
    }

    private RoutingTrace BuildTrace(
        string selectedRouteName,
        string selectedRouteDisplayName,
        double confidence,
        string reason,
        IReadOnlyList<RouteMatch> routeMatches,
        IReadOnlySet<string> selectedRouteNames,
        string? fallbackRouteName)
    {
        var requiresMiniRouter = confidence < MiniRouterConfidenceThreshold;
        return new RoutingTrace
        {
            SelectedRouteName = selectedRouteName,
            SelectedRouteDisplayName = selectedRouteDisplayName,
            Confidence = confidence,
            RoutingMode = requiresMiniRouter ? "rules-low-confidence" : "rules",
            RequiresMiniRouterReview = requiresMiniRouter,
            Reason = reason,
            FallbackRouteName = fallbackRouteName,
            Candidates = routeMatches
                .Select(match => new RoutingCandidateTrace
                {
                    RouteName = match.Route.Name,
                    DisplayName = match.Route.DisplayName,
                    Score = match.Score,
                    MatchedSignals = match.Signals,
                    Selected = selectedRouteNames.Contains(match.Route.Name)
                })
                .ToArray()
        };
    }

    private static double CalculateConfidence(int topScore, int? secondScore, bool isMixed)
    {
        if (topScore <= 0)
        {
            return 0.2;
        }

        if (isMixed)
        {
            return Math.Round(Math.Clamp(0.65 + topScore / (double)(topScore + (secondScore ?? topScore)) * 0.2, 0.65, 0.85), 2);
        }

        if (!secondScore.HasValue || secondScore.Value <= 0)
        {
            return Math.Round(Math.Clamp(0.75 + Math.Min(topScore, 10) / 50d, 0.75, 0.95), 2);
        }

        var separation = (topScore - secondScore.Value) / (double)Math.Max(topScore, 1);
        return Math.Round(Math.Clamp(0.55 + separation * 0.35 + Math.Min(topScore, 10) / 60d, 0.55, 0.98), 2);
    }

    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IReadOnlySet<string> Tokenize(string normalizedPrompt) =>
        TokenRegex.Matches(normalizedPrompt)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(IReadOnlySet<string> tokens, params string[] values) =>
        values.Any(tokens.Contains);

    private static IReadOnlySet<string> BuildSelectedRouteSet(string routeName) =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { routeName };

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

        var primaryRoute = routes.FirstOrDefault();

        return new AgentSessionProfile
        {
            RouteName = _catalog.MixedRoute.Name,
            DisplayName = _catalog.MixedRoute.DisplayName,
            SpecialistPrompt = _catalog.MixedRoute.SpecialistPrompt,
            SkillNames = skillNames,
            Tools = ResolveTools(toolNames, toolCatalog),
            Model = primaryRoute?.Model,
            Provider = BuildProviderConfig(primaryRoute?.Provider)
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
            Tools = ResolveTools(route.ToolNames, toolCatalog),
            Model = route.Model,
            Provider = BuildProviderConfig(route.Provider)
        };
    }

    private static ProviderConfig? BuildProviderConfig(RouteProviderDefinition? provider)
    {
        if (provider is null)
        {
            return null;
        }

        return new ProviderConfig
        {
            Type = provider.Type ?? string.Empty,
            BaseUrl = provider.BaseUrl ?? string.Empty,
            ApiKey = provider.ApiKey,
            WireApi = provider.WireApi ?? string.Empty
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

    private sealed record RouteMatch(AgentRouteDefinition Route, int Score, string[] Signals);
}

public sealed class AgentRoutingDecision
{
    public AgentRoutingDecision(AgentSessionProfile profile, RoutingTrace trace)
    {
        Profile = profile;
        Trace = trace;
    }

    public AgentSessionProfile Profile { get; }
    public RoutingTrace Trace { get; }
}

public sealed class RoutingTrace
{
    public string SelectedRouteName { get; init; } = string.Empty;
    public string SelectedRouteDisplayName { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string RoutingMode { get; init; } = "rules";
    public bool RequiresMiniRouterReview { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string? FallbackRouteName { get; init; }
    public RoutingCandidateTrace[] Candidates { get; init; } = Array.Empty<RoutingCandidateTrace>();
}

public sealed class RoutingCandidateTrace
{
    public string RouteName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Score { get; init; }
    public string[] MatchedSignals { get; init; } = Array.Empty<string>();
    public bool Selected { get; init; }
}
