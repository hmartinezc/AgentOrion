using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface IDatabaseHealthService
{
    Task<DatabaseHealthStatus> CheckAsync(CancellationToken ct = default);
}
