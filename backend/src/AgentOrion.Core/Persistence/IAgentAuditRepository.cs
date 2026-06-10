using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface IAgentAuditRepository
{
    Task<int> CreateAsync(AgentAuditLog auditLog);
    Task<IReadOnlyList<AgentAuditLog>> GetBySessionAsync(string sessionId, int limit = 50);
}
