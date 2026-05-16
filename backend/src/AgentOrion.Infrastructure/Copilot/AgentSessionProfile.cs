using Microsoft.Extensions.AI;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentSessionProfile
{
    public required string RouteName { get; init; }
    public required string DisplayName { get; init; }
    public required string SpecialistPrompt { get; init; }
    public required IReadOnlyList<string> SkillNames { get; init; }
    public required IReadOnlyList<AIFunction> Tools { get; init; }
}
