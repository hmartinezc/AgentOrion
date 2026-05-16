using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface ICustomerRepository
{
    Task<int> CreateAsync(Customer customer);
    Task<Customer?> GetByIdAsync(int id);
    Task<IEnumerable<Customer>> GetAllAsync();
    Task<IEnumerable<Customer>> SearchAsync(string query);
}
