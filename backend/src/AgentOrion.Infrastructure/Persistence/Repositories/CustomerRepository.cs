using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly IAgentOrionDbConnectionFactory _connectionFactory;
    public CustomerRepository(IAgentOrionDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> CreateAsync(Customer customer)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Customers (FullName, Email, Phone, CompanyName, Country, CreatedAt, Address, DocumentNumber)
            VALUES (@fullName, @email, @phone, @company, @country, @createdAt, @address, @documentNumber);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@fullName", customer.FullName);
        cmd.Parameters.AddWithValue("@email", customer.Email ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@phone", customer.Phone ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@company", customer.CompanyName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@country", customer.Country ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", customer.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@address", customer.Address ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@documentNumber", customer.DocumentNumber ?? (object)DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<Customer?> GetByIdAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Customers WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return Map(reader);
        return null;
    }

    public async Task<IEnumerable<Customer>> GetAllAsync()
    {
        var list = new List<Customer>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Customers ORDER BY CreatedAt DESC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(Map(reader));
        return list;
    }

    public async Task<IEnumerable<Customer>> GetRecentAsync(int limit)
    {
        var list = new List<Customer>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Customers ORDER BY CreatedAt DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(Map(reader));
        return list;
    }

    public async Task<IEnumerable<Customer>> SearchAsync(string query)
    {
        var list = new List<Customer>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM Customers 
            WHERE FullName LIKE @q OR Email LIKE @q OR CompanyName LIKE @q
            ORDER BY CreatedAt DESC;";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(Map(reader));
        return list;
    }

    private static Customer Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        FullName = r.GetString(1),
        Email = r.IsDBNull(2) ? null : r.GetString(2),
        Phone = r.IsDBNull(3) ? null : r.GetString(3),
        CompanyName = r.IsDBNull(4) ? null : r.GetString(4),
        Country = r.IsDBNull(5) ? null : r.GetString(5),
        CreatedAt = r.IsDBNull(6) ? DateTime.MinValue : DateTime.Parse(r.GetString(6)),
        Address = r.FieldCount > 7 && !r.IsDBNull(7) ? r.GetString(7) : null,
        DocumentNumber = r.FieldCount > 8 && !r.IsDBNull(8) ? r.GetString(8) : null
    };
}
