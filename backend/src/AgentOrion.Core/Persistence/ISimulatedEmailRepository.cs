using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface ISimulatedEmailRepository
{
    Task<int> CreateAsync(SimulatedEmail email);
    Task<IReadOnlyList<SimulatedEmail>> GetByShipmentIdAsync(int shipmentId);
    Task<IReadOnlyList<SimulatedEmail>> GetByAwbAsync(string awbNumber);
}
