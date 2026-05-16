using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface IShipmentRepository
{
    Task<int> CreateAsync(Shipment shipment);
    Task<Shipment?> GetByAwbAsync(string awbNumber);
    Task<Shipment?> GetByIdAsync(int id);
    Task<IEnumerable<Shipment>> GetAllAsync();
    Task<IEnumerable<Shipment>> GetByCustomerAsync(int customerId);
    Task UpdateStatusAsync(int id, string status);
    Task AddEventAsync(int shipmentId, string eventType, string eventData);
}
