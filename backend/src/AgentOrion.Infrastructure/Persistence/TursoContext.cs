using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence;

public class TursoContext
{
    private readonly string _connectionString;

    public TursoContext(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
        InitializeSchema();
    }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void InitializeSchema()
    {
        using var connection = CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Customers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FullName TEXT NOT NULL,
                Email TEXT,
                Phone TEXT,
                CompanyName TEXT,
                Country TEXT,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP,
                Address TEXT,
                DocumentNumber TEXT
            );

            CREATE TABLE IF NOT EXISTS Shipments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AwbNumber TEXT UNIQUE,
                CustomerId INTEGER REFERENCES Customers(Id),
                ProductType TEXT NOT NULL,
                ProductName TEXT,
                QuantityKg REAL,
                TemperatureRequiredC REAL,
                OriginAirport TEXT,
                DestinationAirport TEXT,
                FlightDate TEXT,
                Status TEXT DEFAULT 'solicitado',
                PhytosanitaryCert TEXT,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS ShipmentEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ShipmentId INTEGER REFERENCES Shipments(Id),
                EventType TEXT,
                EventData TEXT,
                RecordedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS AgentAuditLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT,
                UserPrompt TEXT,
                AgentResponse TEXT,
                ToolCalled TEXT,
                ToolInputJson TEXT,
                ToolOutputJson TEXT,
                WasOffTopic INTEGER DEFAULT 0,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS SimulatedEmails (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ShipmentId INTEGER REFERENCES Shipments(Id),
                RecipientEmail TEXT,
                Subject TEXT,
                Body TEXT,
                SentAt TEXT DEFAULT CURRENT_TIMESTAMP,
                Status TEXT DEFAULT 'simulated'
            );

            CREATE TABLE IF NOT EXISTS ConversationMemory (
                SessionId TEXT PRIMARY KEY,
                LastRouteName TEXT,
                CurrentIntent TEXT,
                CustomerJson TEXT,
                ShipmentJson TEXT,
                UpdatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );
        ";
        cmd.ExecuteNonQuery();

        EnsureColumn(connection, "Customers", "Address", "TEXT");
        EnsureColumn(connection, "Customers", "DocumentNumber", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string columnType)
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = checkCmd.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType};";
        alterCmd.ExecuteNonQuery();
    }
}
