using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence.Repositories;

public class ShipmentEventRepository : IShipmentEventRepository
{
    private readonly IAgentOrionDbConnectionFactory _connectionFactory;

    public ShipmentEventRepository(IAgentOrionDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> AddAsync(ShipmentEvent shipmentEvent)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ShipmentEvents (ShipmentId, EventType, EventData, RecordedAt)
            VALUES (@shipmentId, @eventType, @eventData, @recordedAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@shipmentId", shipmentEvent.ShipmentId);
        cmd.Parameters.AddWithValue("@eventType", shipmentEvent.EventType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@eventData", shipmentEvent.EventData ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@recordedAt", shipmentEvent.RecordedAt.ToString("O"));
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<ShipmentEvent>> GetByShipmentIdAsync(int shipmentId)
    {
        var list = new List<ShipmentEvent>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ShipmentId, EventType, EventData, RecordedAt
            FROM ShipmentEvents
            WHERE ShipmentId = @shipmentId
            ORDER BY RecordedAt ASC;";
        cmd.Parameters.AddWithValue("@shipmentId", shipmentId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<ShipmentEvent>> GetByAwbAsync(string awbNumber)
    {
        var list = new List<ShipmentEvent>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT e.Id, e.ShipmentId, e.EventType, e.EventData, e.RecordedAt
            FROM ShipmentEvents e
            INNER JOIN Shipments s ON s.Id = e.ShipmentId
            WHERE s.AwbNumber = @awbNumber
            ORDER BY e.RecordedAt ASC;";
        cmd.Parameters.AddWithValue("@awbNumber", awbNumber);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    private static ShipmentEvent Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ShipmentId = reader.GetInt32(1),
        EventType = reader.IsDBNull(2) ? null : reader.GetString(2),
        EventData = reader.IsDBNull(3) ? null : reader.GetString(3),
        RecordedAt = reader.IsDBNull(4) ? DateTime.MinValue : DateTime.Parse(reader.GetString(4))
    };
}
