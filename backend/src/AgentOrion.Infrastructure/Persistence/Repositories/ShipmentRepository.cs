using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence.Repositories;

public class ShipmentRepository : IShipmentRepository
{
    private readonly IAgentOrionDbConnectionFactory _connectionFactory;
    public ShipmentRepository(IAgentOrionDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> CreateAsync(Shipment shipment)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Shipments (AwbNumber, CustomerId, ProductType, ProductName, QuantityKg, TemperatureRequiredC,
                OriginAirport, DestinationAirport, FlightDate, Status, PhytosanitaryCert, CreatedAt)
            VALUES (@awb, @custId, @prodType, @prodName, @qty, @temp, @origin, @dest, @flight, @status, @phyto, @createdAt);
            SELECT last_insert_rowid();";
        AddShipmentParams(cmd, shipment);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<int> CreateWithEventAsync(Shipment shipment, string eventType, string eventData)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            using var insertShipment = connection.CreateCommand();
            insertShipment.Transaction = transaction;
            insertShipment.CommandText = @"
                INSERT INTO Shipments (AwbNumber, CustomerId, ProductType, ProductName, QuantityKg, TemperatureRequiredC,
                    OriginAirport, DestinationAirport, FlightDate, Status, PhytosanitaryCert, CreatedAt)
                VALUES (@awb, @custId, @prodType, @prodName, @qty, @temp, @origin, @dest, @flight, @status, @phyto, @createdAt);
                SELECT last_insert_rowid();";
            AddShipmentParams(insertShipment, shipment);
            var result = await insertShipment.ExecuteScalarAsync();
            var shipmentId = Convert.ToInt32(result);

            using var insertEvent = connection.CreateCommand();
            insertEvent.Transaction = transaction;
            insertEvent.CommandText = @"
                INSERT INTO ShipmentEvents (ShipmentId, EventType, EventData, RecordedAt)
                VALUES (@shipId, @type, @data, @recordedAt);";
            insertEvent.Parameters.AddWithValue("@shipId", shipmentId);
            insertEvent.Parameters.AddWithValue("@type", eventType);
            insertEvent.Parameters.AddWithValue("@data", eventData);
            insertEvent.Parameters.AddWithValue("@recordedAt", DateTime.UtcNow.ToString("O"));
            await insertEvent.ExecuteNonQueryAsync();

            transaction.Commit();
            return shipmentId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<Shipment?> GetByAwbAsync(string awbNumber)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Shipments WHERE AwbNumber = @awb;";
        cmd.Parameters.AddWithValue("@awb", awbNumber);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return Map(reader);
        return null;
    }

    public async Task<Shipment?> GetByIdAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Shipments WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return Map(reader);
        return null;
    }

    public async Task<IEnumerable<Shipment>> GetAllAsync()
    {
        var list = new List<Shipment>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Shipments ORDER BY CreatedAt DESC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(Map(reader));
        return list;
    }

    public async Task<IEnumerable<Shipment>> GetRecentAsync(int limit)
    {
        var list = new List<Shipment>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Shipments ORDER BY CreatedAt DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(Map(reader));
        return list;
    }

    public async Task<IEnumerable<Shipment>> GetByCustomerAsync(int customerId)
    {
        var list = new List<Shipment>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Shipments WHERE CustomerId = @custId ORDER BY CreatedAt DESC;";
        cmd.Parameters.AddWithValue("@custId", customerId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(Map(reader));
        return list;
    }

    public async Task UpdateStatusAsync(int id, string status)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Shipments SET Status = @status WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddEventAsync(int shipmentId, string eventType, string eventData)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ShipmentEvents (ShipmentId, EventType, EventData, RecordedAt)
            VALUES (@shipId, @type, @data, @recordedAt);";
        cmd.Parameters.AddWithValue("@shipId", shipmentId);
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@data", eventData);
        cmd.Parameters.AddWithValue("@recordedAt", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddShipmentParams(SqliteCommand cmd, Shipment s)
    {
        cmd.Parameters.AddWithValue("@awb", s.AwbNumber ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@custId", s.CustomerId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@prodType", s.ProductType);
        cmd.Parameters.AddWithValue("@prodName", s.ProductName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@qty", s.QuantityKg ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@temp", s.TemperatureRequiredC ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@origin", s.OriginAirport ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@dest", s.DestinationAirport ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@flight", s.FlightDate?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status", s.Status);
        cmd.Parameters.AddWithValue("@phyto", s.PhytosanitaryCert ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", s.CreatedAt.ToString("O"));
    }

    private static Shipment Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        AwbNumber = r.IsDBNull(1) ? null : r.GetString(1),
        CustomerId = r.IsDBNull(2) ? null : r.GetInt32(2),
        ProductType = r.GetString(3),
        ProductName = r.IsDBNull(4) ? null : r.GetString(4),
        QuantityKg = r.IsDBNull(5) ? null : r.GetDouble(5),
        TemperatureRequiredC = r.IsDBNull(6) ? null : r.GetDouble(6),
        OriginAirport = r.IsDBNull(7) ? null : r.GetString(7),
        DestinationAirport = r.IsDBNull(8) ? null : r.GetString(8),
        FlightDate = r.IsDBNull(9) ? null : DateTime.Parse(r.GetString(9)),
        Status = r.GetString(10),
        PhytosanitaryCert = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt = r.IsDBNull(12) ? DateTime.MinValue : DateTime.Parse(r.GetString(12))
    };
}
