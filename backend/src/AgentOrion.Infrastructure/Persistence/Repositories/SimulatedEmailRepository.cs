using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence.Repositories;

public class SimulatedEmailRepository : ISimulatedEmailRepository
{
    private readonly IAgentOrionDbConnectionFactory _connectionFactory;

    public SimulatedEmailRepository(IAgentOrionDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> CreateAsync(SimulatedEmail email)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO SimulatedEmails (ShipmentId, RecipientEmail, Subject, Body, SentAt, Status)
                VALUES (@shipmentId, @recipientEmail, @subject, @body, @sentAt, @status);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@shipmentId", email.ShipmentId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@recipientEmail", email.RecipientEmail ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@subject", email.Subject ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@body", email.Body ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sentAt", email.SentAt.ToString("O"));
            cmd.Parameters.AddWithValue("@status", email.Status);
            var result = await cmd.ExecuteScalarAsync();
            transaction.Commit();
            return Convert.ToInt32(result);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<SimulatedEmail>> GetByShipmentIdAsync(int shipmentId)
    {
        var list = new List<SimulatedEmail>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ShipmentId, RecipientEmail, Subject, Body, SentAt, Status
            FROM SimulatedEmails
            WHERE ShipmentId = @shipmentId
            ORDER BY SentAt DESC;";
        cmd.Parameters.AddWithValue("@shipmentId", shipmentId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<SimulatedEmail>> GetByAwbAsync(string awbNumber)
    {
        var list = new List<SimulatedEmail>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT e.Id, e.ShipmentId, e.RecipientEmail, e.Subject, e.Body, e.SentAt, e.Status
            FROM SimulatedEmails e
            INNER JOIN Shipments s ON s.Id = e.ShipmentId
            WHERE s.AwbNumber = @awbNumber
            ORDER BY e.SentAt DESC;";
        cmd.Parameters.AddWithValue("@awbNumber", awbNumber);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    private static SimulatedEmail Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ShipmentId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
        RecipientEmail = reader.IsDBNull(2) ? null : reader.GetString(2),
        Subject = reader.IsDBNull(3) ? null : reader.GetString(3),
        Body = reader.IsDBNull(4) ? null : reader.GetString(4),
        SentAt = reader.IsDBNull(5) ? DateTime.MinValue : DateTime.Parse(reader.GetString(5)),
        Status = reader.IsDBNull(6) ? "simulated" : reader.GetString(6)
    };
}
