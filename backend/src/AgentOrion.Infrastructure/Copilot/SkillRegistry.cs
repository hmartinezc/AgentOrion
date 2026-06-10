using AgentOrion.Core.Configuration;
using Microsoft.Extensions.Options;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class SkillRegistry
{
    private readonly IReadOnlyDictionary<string, string> _skillPathByName;

    public SkillRegistry(IOptions<AgentOrionOptions> options, string contentRootPath)
    {
        _skillPathByName = BuildSkillPathIndex(options.Value.SkillDirectories, contentRootPath);
    }

    public IReadOnlyCollection<string> SkillNames => _skillPathByName.Keys.ToArray();

    public IReadOnlyList<string> ResolveDirectories(IEnumerable<string> skillNames)
    {
        var resolved = new List<string>();
        var missing = new List<string>();

        foreach (var skillName in skillNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_skillPathByName.TryGetValue(skillName, out var path))
            {
                resolved.Add(path);
                continue;
            }

            missing.Add(skillName);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Agent catalog references missing skills: {string.Join(", ", missing)}");
        }

        return resolved;
    }

    public void ValidateCatalog(AgentCatalog catalog)
    {
        var missing = catalog.Routes
            .SelectMany(route => route.SkillNames.Select(skillName => new { route.Name, SkillName = skillName }))
            .Where(item => !_skillPathByName.ContainsKey(item.SkillName))
            .Select(item => $"{item.Name}:{item.SkillName}")
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Agent catalog references missing skills: {string.Join(", ", missing)}");
        }
    }

    private static IReadOnlyDictionary<string, string> BuildSkillPathIndex(IEnumerable<string> configuredDirectories, string contentRootPath)
    {
        var roots = configuredDirectories
            .Select(path => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(contentRootPath, path)))
            .Where(Directory.Exists)
            .ToArray();

        if (roots.Length == 0)
        {
            throw new InvalidOperationException("No valid AgentOrion skill directories were found.");
        }

        return roots
            .SelectMany(Directory.GetDirectories)
            .Where(path => File.Exists(Path.Combine(path, "SKILL.md")))
            .Select(path => new { Name = Path.GetFileName(path), Path = path })
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
            .ToDictionary(
                skill => skill.Name!,
                skill => skill.Path,
                StringComparer.OrdinalIgnoreCase);
    }
}
