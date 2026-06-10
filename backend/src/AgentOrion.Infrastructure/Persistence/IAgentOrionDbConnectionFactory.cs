using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence;

public interface IAgentOrionDbConnectionFactory
{
    string DatabasePath { get; }
    SqliteConnection CreateConnection();
}
