using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Persistence;

public class DatabaseHealthService : IDatabaseHealthService
{
    private readonly IAgentOrionDbConnectionFactory _connectionFactory;

    public DatabaseHealthService(IAgentOrionDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<DatabaseHealthStatus> CheckAsync(CancellationToken ct = default)
    {
        var status = new DatabaseHealthStatus
        {
            Provider = "sqlite",
            DatabasePath = _connectionFactory.DatabasePath
        };

        try
        {
            using var connection = _connectionFactory.CreateConnection();
            status.CanConnect = true;
            status.SchemaVersion = Convert.ToInt32(await ScalarAsync(connection, "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;", ct) ?? 0);
            status.JournalMode = Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode;", ct));
            status.ForeignKeysEnabled = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA foreign_keys;", ct) ?? 0) == 1;
            status.BusyTimeoutMs = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA busy_timeout;", ct) ?? 0);
        }
        catch (Exception ex)
        {
            status.CanConnect = false;
            status.Error = ex.Message;
        }

        return status;
    }

    private static async Task<object?> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }
}
