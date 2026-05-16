using System.Text.Json;
using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Persistence;

namespace AgentOrion.Infrastructure.Persistence.Repositories;

public class ConversationMemoryRepository : IConversationMemoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TursoContext _context;

    public ConversationMemoryRepository(TursoContext context) => _context = context;

    public async Task<ConversationMemoryState?> GetAsync(string sessionId)
    {
        using var connection = _context.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT SessionId, LastRouteName, CurrentIntent, CustomerJson, ShipmentJson, UpdatedAt
            FROM ConversationMemory
            WHERE SessionId = @sessionId;";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ConversationMemoryState
        {
            SessionId = reader.GetString(0),
            LastRouteName = reader.IsDBNull(1) ? null : reader.GetString(1),
            CurrentIntent = reader.IsDBNull(2) ? null : reader.GetString(2),
            Customer = reader.IsDBNull(3)
                ? new ConversationCustomerMemory()
                : JsonSerializer.Deserialize<ConversationCustomerMemory>(reader.GetString(3), JsonOptions) ?? new ConversationCustomerMemory(),
            Shipment = reader.IsDBNull(4)
                ? new ConversationShipmentMemory()
                : JsonSerializer.Deserialize<ConversationShipmentMemory>(reader.GetString(4), JsonOptions) ?? new ConversationShipmentMemory(),
            UpdatedAt = reader.IsDBNull(5) ? DateTime.UtcNow : DateTime.Parse(reader.GetString(5))
        };
    }

    public async Task UpsertAsync(ConversationMemoryState state)
    {
        using var connection = _context.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ConversationMemory (SessionId, LastRouteName, CurrentIntent, CustomerJson, ShipmentJson, UpdatedAt)
            VALUES (@sessionId, @lastRouteName, @currentIntent, @customerJson, @shipmentJson, @updatedAt)
            ON CONFLICT(SessionId) DO UPDATE SET
                LastRouteName = excluded.LastRouteName,
                CurrentIntent = excluded.CurrentIntent,
                CustomerJson = excluded.CustomerJson,
                ShipmentJson = excluded.ShipmentJson,
                UpdatedAt = excluded.UpdatedAt;";

        cmd.Parameters.AddWithValue("@sessionId", state.SessionId);
        cmd.Parameters.AddWithValue("@lastRouteName", state.LastRouteName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@currentIntent", state.CurrentIntent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@customerJson", JsonSerializer.Serialize(state.Customer, JsonOptions));
        cmd.Parameters.AddWithValue("@shipmentJson", JsonSerializer.Serialize(state.Shipment, JsonOptions));
        cmd.Parameters.AddWithValue("@updatedAt", state.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string sessionId)
    {
        using var connection = _context.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ConversationMemory WHERE SessionId = @sessionId;";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }
}
