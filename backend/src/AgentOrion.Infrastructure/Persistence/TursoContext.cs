using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence;

public class TursoContext : IAgentOrionDbConnectionFactory
{
    public const int CurrentSchemaVersion = 3;
    private const int BusyTimeoutMs = 5000;
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public TursoContext(string dbPath)
    {
        DatabasePath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);
        return connection;
    }

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();
        EnsureMigrationTable(connection);
        ApplyMigration(connection, 1, "initial_schema", ApplyInitialSchema);
        ApplyMigration(connection, 2, "operational_hardening", ApplyOperationalHardening);
        ApplyMigration(connection, 3, "routing_trace_audit", ApplyRoutingTraceAudit);
    }

    private static void EnsureMigrationTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAt TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    private static void ApplyMigration(SqliteConnection connection, int version, string name, Action<SqliteConnection> migration)
    {
        if (IsMigrationApplied(connection, version))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            migration(connection);

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO SchemaMigrations (Version, Name, AppliedAt)
                VALUES (@version, @name, @appliedAt);";
            cmd.Parameters.AddWithValue("@version", version);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@appliedAt", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static bool IsMigrationApplied(SqliteConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM SchemaMigrations WHERE Version = @version;";
        cmd.Parameters.AddWithValue("@version", version);
        return cmd.ExecuteScalar() is not null;
    }

    private static void ApplyInitialSchema(SqliteConnection connection)
    {
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
                RouteName TEXT,
                RouteDisplayName TEXT,
                Model TEXT,
                ChatMode TEXT,
                ToolsJson TEXT,
                SkillsJson TEXT,
                Error TEXT,
                UsageJson TEXT,
                DurationMs REAL,
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
    }

    private static void ApplyOperationalHardening(SqliteConnection connection)
    {
        EnsureColumn(connection, "Customers", "Address", "TEXT");
        EnsureColumn(connection, "Customers", "DocumentNumber", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "RouteName", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "RouteDisplayName", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "Model", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "ChatMode", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "ToolsJson", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "SkillsJson", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "Error", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "UsageJson", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "DurationMs", "REAL");

        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Customers_FullName ON Customers(FullName);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Customers_Email ON Customers(Email);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Customers_CompanyName ON Customers(CompanyName);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Customers_CreatedAt ON Customers(CreatedAt);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Shipments_AwbNumber ON Shipments(AwbNumber);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Shipments_CustomerId ON Shipments(CustomerId);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Shipments_Status ON Shipments(Status);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_Shipments_CreatedAt ON Shipments(CreatedAt);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_ShipmentEvents_ShipmentId ON ShipmentEvents(ShipmentId);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_ShipmentEvents_RecordedAt ON ShipmentEvents(RecordedAt);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_SimulatedEmails_ShipmentId ON SimulatedEmails(ShipmentId);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_ConversationMemory_UpdatedAt ON ConversationMemory(UpdatedAt);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_AgentAuditLog_SessionId ON AgentAuditLog(SessionId);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_AgentAuditLog_CreatedAt ON AgentAuditLog(CreatedAt);");
    }

    private static void ApplyRoutingTraceAudit(SqliteConnection connection)
    {
        EnsureColumn(connection, "AgentAuditLog", "RoutingTraceJson", "TEXT");
        EnsureColumn(connection, "AgentAuditLog", "RouteConfidence", "REAL");
        EnsureColumn(connection, "AgentAuditLog", "RoutingReason", "TEXT");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_AgentAuditLog_RouteName ON AgentAuditLog(RouteName);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS IX_AgentAuditLog_RouteConfidence ON AgentAuditLog(RouteConfidence);");
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

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout={BusyTimeoutMs};");
        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
    }
}
