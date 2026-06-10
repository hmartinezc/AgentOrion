using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Persistence.Repositories;

public class AgentAuditRepository : IAgentAuditRepository
{
    private readonly IAgentOrionDbConnectionFactory _connectionFactory;

    public AgentAuditRepository(IAgentOrionDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> CreateAsync(AgentAuditLog auditLog)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AgentAuditLog (
                SessionId, UserPrompt, AgentResponse, ToolCalled, ToolInputJson, ToolOutputJson,
                RouteName, RouteDisplayName, Model, ChatMode, ToolsJson, SkillsJson, Error,
                UsageJson, RoutingTraceJson, RouteConfidence, RoutingReason, DurationMs, WasOffTopic, CreatedAt)
            VALUES (
                @sessionId, @userPrompt, @agentResponse, @toolCalled, @toolInputJson, @toolOutputJson,
                @routeName, @routeDisplayName, @model, @chatMode, @toolsJson, @skillsJson, @error,
                @usageJson, @routingTraceJson, @routeConfidence, @routingReason, @durationMs, @wasOffTopic, @createdAt);
            SELECT last_insert_rowid();";

        AddParameters(cmd, auditLog);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<AgentAuditLog>> GetBySessionAsync(string sessionId, int limit = 50)
    {
        var list = new List<AgentAuditLog>();
        using var connection = _connectionFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, SessionId, UserPrompt, AgentResponse, ToolCalled, ToolInputJson, ToolOutputJson,
                   RouteName, RouteDisplayName, Model, ChatMode, ToolsJson, SkillsJson, Error,
                   UsageJson, RoutingTraceJson, RouteConfidence, RoutingReason, DurationMs, WasOffTopic, CreatedAt
            FROM AgentAuditLog
            WHERE SessionId = @sessionId
            ORDER BY CreatedAt DESC
            LIMIT @limit;";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    private static void AddParameters(SqliteCommand cmd, AgentAuditLog auditLog)
    {
        cmd.Parameters.AddWithValue("@sessionId", auditLog.SessionId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@userPrompt", auditLog.UserPrompt ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@agentResponse", auditLog.AgentResponse ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@toolCalled", auditLog.ToolCalled ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@toolInputJson", auditLog.ToolInputJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@toolOutputJson", auditLog.ToolOutputJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@routeName", auditLog.RouteName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@routeDisplayName", auditLog.RouteDisplayName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@model", auditLog.Model ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@chatMode", auditLog.ChatMode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@toolsJson", auditLog.ToolsJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@skillsJson", auditLog.SkillsJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@error", auditLog.Error ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@usageJson", auditLog.UsageJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@routingTraceJson", auditLog.RoutingTraceJson ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@routeConfidence", auditLog.RouteConfidence ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@routingReason", auditLog.RoutingReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@durationMs", auditLog.DurationMs ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@wasOffTopic", auditLog.WasOffTopic ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", auditLog.CreatedAt.ToString("O"));
    }

    private static AgentAuditLog Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        SessionId = reader.IsDBNull(1) ? null : reader.GetString(1),
        UserPrompt = reader.IsDBNull(2) ? null : reader.GetString(2),
        AgentResponse = reader.IsDBNull(3) ? null : reader.GetString(3),
        ToolCalled = reader.IsDBNull(4) ? null : reader.GetString(4),
        ToolInputJson = reader.IsDBNull(5) ? null : reader.GetString(5),
        ToolOutputJson = reader.IsDBNull(6) ? null : reader.GetString(6),
        RouteName = reader.IsDBNull(7) ? null : reader.GetString(7),
        RouteDisplayName = reader.IsDBNull(8) ? null : reader.GetString(8),
        Model = reader.IsDBNull(9) ? null : reader.GetString(9),
        ChatMode = reader.IsDBNull(10) ? null : reader.GetString(10),
        ToolsJson = reader.IsDBNull(11) ? null : reader.GetString(11),
        SkillsJson = reader.IsDBNull(12) ? null : reader.GetString(12),
        Error = reader.IsDBNull(13) ? null : reader.GetString(13),
        UsageJson = reader.IsDBNull(14) ? null : reader.GetString(14),
        RoutingTraceJson = reader.IsDBNull(15) ? null : reader.GetString(15),
        RouteConfidence = reader.IsDBNull(16) ? null : reader.GetDouble(16),
        RoutingReason = reader.IsDBNull(17) ? null : reader.GetString(17),
        DurationMs = reader.IsDBNull(18) ? null : reader.GetDouble(18),
        WasOffTopic = !reader.IsDBNull(19) && reader.GetInt32(19) == 1,
        CreatedAt = reader.IsDBNull(20) ? DateTime.MinValue : DateTime.Parse(reader.GetString(20))
    };
}
