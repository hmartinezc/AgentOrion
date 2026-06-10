using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface IShipmentEventRepository
{
    Task<int> AddAsync(ShipmentEvent shipmentEvent);
    Task<IReadOnlyList<ShipmentEvent>> GetByShipmentIdAsync(int shipmentId);
    Task<IReadOnlyList<ShipmentEvent>> GetByAwbAsync(string awbNumber);
}
