using GitHub.Copilot.SDK;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentResponseTraceBuilder
{
    private readonly object _sync = new();
    private readonly string _sessionId;
    private readonly string _prompt;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, string> _toolNamesByCallId = new(StringComparer.Ordinal);
    private readonly List<string> _tools = new();
    private readonly List<string> _skills = new();
    private readonly List<string> _subagents = new();
    private readonly Dictionary<string, AgentToolExecutionTraceBuilder> _toolExecutionsByCallId = new(StringComparer.Ordinal);
    private string? _routeName;
    private string? _routeDisplayName;
    private string? _chatMode;
    private RoutingTrace? _routing;

    private DateTimeOffset? _completedAt;
    private string _model;
    private string? _error;
    private double? _inputTokens;
    private double? _outputTokens;
    private double? _cacheReadTokens;
    private double? _cacheWriteTokens;
    private double? _cost;
    private double? _sessionCurrentTokens;
    private double? _sessionTokenLimit;
    private double? _messagesInContext;

    public AgentResponseTraceBuilder(string sessionId, string prompt, string model)
    {
        _sessionId = sessionId;
        _prompt = prompt;
        _model = model;
    }

    public void RecordToolStart(string? toolCallId, string? toolName)
    {
        lock (_sync)
        {
            AddUnique(_tools, toolName);

            if (!string.IsNullOrWhiteSpace(toolCallId) && !string.IsNullOrWhiteSpace(toolName))
            {
                _toolNamesByCallId[toolCallId] = toolName;
                _toolExecutionsByCallId[toolCallId] = new AgentToolExecutionTraceBuilder
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    StartedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    public void RecordToolPermission(string? toolCallId, bool requiresConfirmation)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(toolCallId) &&
                _toolExecutionsByCallId.TryGetValue(toolCallId, out var execution))
            {
                execution.RequiresConfirmation = requiresConfirmation;
            }
        }
    }

    public void RecordTool(string? toolName)
    {
        lock (_sync)
        {
            AddUnique(_tools, toolName);
        }
    }

    public string? RecordToolCompletion(string? toolCallId)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(toolCallId) && _toolNamesByCallId.TryGetValue(toolCallId, out var toolName))
            {
                AddUnique(_tools, toolName);
                return toolName;
            }

            return null;
        }
    }

    public void RecordToolResult(string? toolCallId, string? toolName, bool? success, string? error, string? resultPreview)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                return;
            }

            if (!_toolExecutionsByCallId.TryGetValue(toolCallId, out var execution))
            {
                execution = new AgentToolExecutionTraceBuilder
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    StartedAt = DateTimeOffset.UtcNow
                };
                _toolExecutionsByCallId[toolCallId] = execution;
            }

            execution.ToolName = toolName ?? execution.ToolName;
            execution.Success = success;
            execution.Error = error;
            execution.CompletedAt = DateTimeOffset.UtcNow;
            execution.ResultPreview = Truncate(resultPreview, 2000);
        }
    }

    public void RecordSkill(string? skillName)
    {
        lock (_sync)
        {
            AddUnique(_skills, skillName);
        }
    }

    public void RecordRoute(string routeName, string displayName)
    {
        lock (_sync)
        {
            _routeName = routeName;
            _routeDisplayName = displayName;
        }
    }

    public void RecordRouting(RoutingTrace routing)
    {
        lock (_sync)
        {
            _routing = routing;
            _routeName = routing.SelectedRouteName;
            _routeDisplayName = routing.SelectedRouteDisplayName;
        }
    }

    public void RecordMode(string chatMode)
    {
        lock (_sync)
        {
            _chatMode = chatMode;
        }
    }

    public void RecordSubagent(string? agentName)
    {
        lock (_sync)
        {
            AddUnique(_subagents, agentName);
        }
    }

    public void RecordUsage(AssistantUsageData usage)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(usage.Model))
            {
                _model = usage.Model;
            }

            Add(ref _inputTokens, usage.InputTokens);
            Add(ref _outputTokens, usage.OutputTokens);
            Add(ref _cacheReadTokens, usage.CacheReadTokens);
            Add(ref _cacheWriteTokens, usage.CacheWriteTokens);
            Add(ref _cost, usage.Cost);
        }
    }

    public void RecordSessionUsage(SessionUsageInfoData usage)
    {
        lock (_sync)
        {
            _sessionCurrentTokens = usage.CurrentTokens;
            _sessionTokenLimit = usage.TokenLimit;
            _messagesInContext = usage.MessagesLength;
        }
    }

    public void RecordError(string error)
    {
        lock (_sync)
        {
            _error = error;
        }
    }

    public void Complete()
    {
        lock (_sync)
        {
            _completedAt ??= DateTimeOffset.UtcNow;
        }
    }

    public AgentResponseTrace Build()
    {
        lock (_sync)
        {
            var completedAt = _completedAt ?? DateTimeOffset.UtcNow;

            return new AgentResponseTrace
            {
                SessionId = _sessionId,
                Prompt = _prompt,
                StartedAt = _startedAt,
                CompletedAt = completedAt,
                DurationMs = (completedAt - _startedAt).TotalMilliseconds,
                Model = _model,
                ChatMode = _chatMode,
                RouteName = _routeName,
                RouteDisplayName = _routeDisplayName,
                Routing = _routing,
                Tools = _tools.ToArray(),
                ToolExecutions = _toolExecutionsByCallId.Values
                    .Select(execution => execution.Build())
                    .ToArray(),
                Skills = _skills.ToArray(),
                Subagents = _subagents.ToArray(),
                Error = _error,
                Usage = BuildUsage()
            };
        }
    }

    private AgentResponseUsageTrace? BuildUsage()
    {
        var totalTokens = (_inputTokens ?? 0) + (_outputTokens ?? 0);
        var hasUsage =
            _inputTokens.HasValue ||
            _outputTokens.HasValue ||
            _cacheReadTokens.HasValue ||
            _cacheWriteTokens.HasValue ||
            _cost.HasValue ||
            _sessionCurrentTokens.HasValue ||
            _sessionTokenLimit.HasValue ||
            _messagesInContext.HasValue;

        if (!hasUsage)
        {
            return null;
        }

        return new AgentResponseUsageTrace
        {
            InputTokens = _inputTokens,
            OutputTokens = _outputTokens,
            TotalTokens = totalTokens > 0 ? totalTokens : null,
            CacheReadTokens = _cacheReadTokens,
            CacheWriteTokens = _cacheWriteTokens,
            Cost = _cost,
            SessionCurrentTokens = _sessionCurrentTokens,
            SessionTokenLimit = _sessionTokenLimit,
            MessagesInContext = _messagesInContext
        };
    }

    private static void Add(ref double? target, double? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        target = (target ?? 0) + value.Value;
    }

    private static void AddUnique(List<string> items, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var item in items)
        {
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        items.Add(value);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed class AgentToolExecutionTraceBuilder
    {
        public string ToolCallId { get; init; } = string.Empty;
        public string? ToolName { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool? Success { get; set; }
        public string? Error { get; set; }
        public string? ResultPreview { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }

        public AgentToolExecutionTrace Build()
        {
            var completedAt = CompletedAt ?? DateTimeOffset.UtcNow;
            return new AgentToolExecutionTrace
            {
                ToolCallId = ToolCallId,
                ToolName = ToolName,
                RequiresConfirmation = RequiresConfirmation,
                Success = Success,
                Error = Error,
                ResultPreview = ResultPreview,
                DurationMs = (completedAt - StartedAt).TotalMilliseconds
            };
        }
    }
}

public sealed class AgentResponseTrace
{
    public string SessionId { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public double DurationMs { get; init; }
    public string Model { get; init; } = string.Empty;
    public string? ChatMode { get; init; }
    public string? RouteName { get; init; }
    public string? RouteDisplayName { get; init; }
    public RoutingTrace? Routing { get; init; }
    public string[] Tools { get; init; } = Array.Empty<string>();
    public AgentToolExecutionTrace[] ToolExecutions { get; init; } = Array.Empty<AgentToolExecutionTrace>();
    public string[] Skills { get; init; } = Array.Empty<string>();
    public string[] Subagents { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public AgentResponseUsageTrace? Usage { get; init; }
}

public sealed class AgentToolExecutionTrace
{
    public string ToolCallId { get; init; } = string.Empty;
    public string? ToolName { get; init; }
    public bool RequiresConfirmation { get; init; }
    public bool? Success { get; init; }
    public string? Error { get; init; }
    public string? ResultPreview { get; init; }
    public double DurationMs { get; init; }
}

public sealed class AgentResponseUsageTrace
{
    public double? InputTokens { get; init; }
    public double? OutputTokens { get; init; }
    public double? TotalTokens { get; init; }
    public double? CacheReadTokens { get; init; }
    public double? CacheWriteTokens { get; init; }
    public double? Cost { get; init; }
    public double? SessionCurrentTokens { get; init; }
    public double? SessionTokenLimit { get; init; }
    public double? MessagesInContext { get; init; }
}
