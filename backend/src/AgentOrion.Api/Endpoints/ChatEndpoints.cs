using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentOrion.Core.Configuration;
using AgentOrion.Infrastructure.Copilot;
using AgentOrion.Infrastructure.Persistence;
using AgentOrion.Infrastructure.Tools;
using AgentOrion.Core.Persistence;
using AgentOrion.Core.Models;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Options;

namespace AgentOrion.Api.Endpoints;

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/reset", async (
            ResetChatRequest request,
            AgentFactory agentFactory,
            IConversationMemoryRepository memoryRepository,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                await agentFactory.DeleteSessionAsync(request.SessionId, ct);
                await memoryRepository.DeleteAsync(request.SessionId);
            }

            return Results.Ok();
        });

        app.MapPost("/api/chat", async (
            ChatRequest request,
            AgentFactory agentFactory,
            ConversationMemoryService memoryService,
            ConversationTurnCoordinator turnCoordinator,
            IOptions<AgentOrionOptions> options,
            ICustomerRepository customers,
            IConversationMemoryRepository memoryRepository,
            IShipmentRepository shipments,
            TursoContext tursoContext,
            AgentCatalogProvider catalogProvider,
            CancellationToken ct,
            HttpContext httpContext) =>
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.Append("Cache-Control", "no-cache");
            httpContext.Response.Headers.Append("Connection", "keep-alive");

            var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N")[..12];
            var chatMode = ChatModes.Normalize(request.Mode);
            var trace = new ResponseTraceBuilder(sessionId, request.Message, options.Value.Copilot.Model);
            trace.RecordMode(chatMode);
            await using var turnLease = await turnCoordinator.AcquireAsync(sessionId, ct);
            var memoryState = chatMode == ChatModes.Memory
                ? await memoryRepository.GetAsync(sessionId) ?? memoryService.Create(sessionId)
                : null;

            if (memoryState is not null)
            {
                memoryService.ApplyPrompt(memoryState, request.Message);
            }

            var deterministicResult = await DeterministicTurnHandler.TryHandleAsync(
                request.Message,
                memoryState,
                memoryService,
                customers,
                shipments,
                ct);

            if (deterministicResult is not null)
            {
                trace.RecordRoute(deterministicResult.RouteName, deterministicResult.RouteDisplayName);
                var routeDef = catalogProvider.Catalog.Routes
                    .FirstOrDefault(r => string.Equals(r.Name, deterministicResult.RouteName, StringComparison.OrdinalIgnoreCase));
                if (routeDef is not null)
                {
                    foreach (var skillName in routeDef.SkillNames)
                    {
                        trace.RecordSkill(skillName);
                    }
                }

                foreach (var tool in deterministicResult.Tools)
                {
                    trace.RecordTool(tool);
                }

                if (memoryState is not null)
                {
                    memoryState.LastRouteName = deterministicResult.RouteName;
                    memoryState.UpdatedAt = DateTime.UtcNow;
                    await memoryRepository.UpsertAsync(memoryState);
                }

                trace.Complete();
                await WriteSseAsync(httpContext, new { type = "delta", content = deterministicResult.Content }, ct);
                await WriteSseAsync(httpContext, new { type = "final", content = deterministicResult.Content }, ct);
                await WriteSseAsync(httpContext, new { type = "trace", trace = trace.Build() }, ct);
                await WriteSseAsync(httpContext, new { type = "done" }, ct);
                return Results.Empty;
            }

            // Crear sesión con BYOK + Tools
            var tools = new[]
            {
                AwbTools.CreateCreateAwbTool(shipments),
                AwbTools.CreateGetAwbStatusTool(shipments),
                AwbTools.CreateGetTemperatureRequirementsTool(),
                CustomerTools.CreateRegisterCustomerTool(customers),
                CustomerTools.CreateSearchCustomerTool(customers),
                NotificationTools.CreateSimulateEmailTool(tursoContext)
            };
            var sessionHandle = await agentFactory.GetOrCreateSessionAsync(
                sessionId,
                request.Message,
                chatMode,
                memoryContextAppendix: memoryService.BuildContextAppendix(memoryState),
                fallbackRouteName: memoryState?.LastRouteName,
                tools: tools,
                ct: ct);
            var session = sessionHandle.Session;
            trace.RecordRoute(sessionHandle.Profile.RouteName, sessionHandle.Profile.DisplayName);
            foreach (var skillName in sessionHandle.Profile.SkillNames)
            {
                trace.RecordSkill(skillName);
            }

            if (memoryState is not null)
            {
                memoryState.LastRouteName = sessionHandle.Profile.RouteName;
            }

            // Stream eventos via channel para evitar sync I/O sobre Response.Body
            var stream = Channel.CreateUnbounded<string>();
            var subscription = session.On(evt =>
            {
                if (ct.IsCancellationRequested) return;

                string? payload = null;

                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        payload = SerializeSse(new { type = "delta", content = delta.Data.DeltaContent });
                        break;
                    case AssistantMessageEvent msg:
                        payload = SerializeSse(new { type = "final", content = msg.Data.Content });
                        break;
                    case ToolExecutionStartEvent toolStart:
                        trace.RecordToolStart(toolStart.Data.ToolCallId, toolStart.Data.ToolName);
                        payload = SerializeSse(new { type = "tool_start", tool = toolStart.Data.ToolName });
                        break;
                    case ToolExecutionCompleteEvent toolDone:
                    {
                        var completedToolName = trace.RecordToolCompletion(toolDone.Data.ToolCallId);
                        if (memoryState is not null)
                        {
                            memoryService.ApplyToolResult(
                                memoryState,
                                completedToolName ?? string.Empty,
                                toolDone.Data.Result?.DetailedContent ?? toolDone.Data.Result?.Content);
                        }
                        payload = SerializeSse(new
                        {
                            type = "tool_done",
                            tool = completedToolName,
                            success = toolDone.Data.Success,
                            error = toolDone.Data.Error?.Message
                        });
                        break;
                    }
                    case SkillInvokedEvent skillInvoked:
                        trace.RecordSkill(skillInvoked.Data.Name);
                        break;
                    case SubagentSelectedEvent selected:
                        trace.RecordSubagent(selected.Data.AgentDisplayName ?? selected.Data.AgentName);
                        payload = SerializeSse(new { type = "subagent_selected", agent = selected.Data.AgentDisplayName ?? selected.Data.AgentName });
                        break;
                    case SubagentStartedEvent started:
                        trace.RecordSubagent(started.Data.AgentDisplayName ?? started.Data.AgentName);
                        payload = SerializeSse(new { type = "subagent_started", agent = started.Data.AgentDisplayName ?? started.Data.AgentName });
                        break;
                    case SubagentCompletedEvent completed:
                        trace.RecordSubagent(completed.Data.AgentDisplayName ?? completed.Data.AgentName);
                        payload = SerializeSse(new { type = "subagent_completed", agent = completed.Data.AgentDisplayName ?? completed.Data.AgentName });
                        break;
                    case SubagentFailedEvent failed:
                        trace.RecordSubagent(failed.Data.AgentDisplayName ?? failed.Data.AgentName);
                        payload = SerializeSse(new { type = "subagent_failed", agent = failed.Data.AgentDisplayName ?? failed.Data.AgentName, error = failed.Data.Error });
                        break;
                    case AssistantUsageEvent usage:
                        trace.RecordUsage(usage.Data);
                        break;
                    case SessionUsageInfoEvent usageInfo:
                        trace.RecordSessionUsage(usageInfo.Data);
                        break;
                }

                if (payload != null)
                {
                    stream.Writer.TryWrite(payload);
                }
            });

            var writerTask = Task.Run(async () =>
            {
                await foreach (var payload in stream.Reader.ReadAllAsync(ct))
                {
                    var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
                    await httpContext.Response.Body.WriteAsync(bytes, ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
            }, ct);

            try
            {
                try
                {
                    await session.SendAndWaitAsync(new MessageOptions { Prompt = request.Message });
                }
                catch (Exception ex)
                {
                    trace.RecordError(ex.Message);
                    stream.Writer.TryWrite(SerializeSse(new { type = "error", content = ex.Message }));
                }
                finally
                {
                    if (sessionHandle.IsTransient)
                    {
                        await agentFactory.DeleteSessionAsync(session.SessionId, ct);
                    }

                    if (memoryState is not null)
                    {
                        memoryState.UpdatedAt = DateTime.UtcNow;
                        await memoryRepository.UpsertAsync(memoryState);
                    }

                    trace.Complete();
                    stream.Writer.TryWrite(SerializeSse(new { type = "trace", trace = trace.Build() }));
                    stream.Writer.TryWrite(SerializeSse(new { type = "done" }));
                    stream.Writer.TryComplete();
                }

                await writerTask;
            }
            finally
            {
                subscription.Dispose();
            }

            return Results.Empty;
        });

        return app;
    }

    private static async Task WriteSseAsync(HttpContext httpContext, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {SerializeSse(payload)}\n\n");
        await httpContext.Response.Body.WriteAsync(bytes, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }

    private static string SerializeSse(object payload) => JsonSerializer.Serialize(payload, SseJsonOptions);
}

public record ChatRequest(string Message, string? SessionId = null, string? Mode = null);
public record ResetChatRequest(string SessionId);

file sealed class ResponseTraceBuilder
{
    private readonly object _sync = new();
    private readonly string _sessionId;
    private readonly string _prompt;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, string> _toolNamesByCallId = new(StringComparer.Ordinal);
    private readonly List<string> _tools = new();
    private readonly List<string> _skills = new();
    private readonly List<string> _subagents = new();
    private string? _routeName;
    private string? _routeDisplayName;
    private string? _chatMode;

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

    public ResponseTraceBuilder(string sessionId, string prompt, string model)
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

    public ResponseTrace Build()
    {
        lock (_sync)
        {
            var completedAt = _completedAt ?? DateTimeOffset.UtcNow;

            return new ResponseTrace
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
                Tools = _tools.ToArray(),
                Skills = _skills.ToArray(),
                Subagents = _subagents.ToArray(),
                Error = _error,
                Usage = BuildUsage()
            };
        }
    }

    private ResponseUsageTrace? BuildUsage()
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

        return new ResponseUsageTrace
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
}

file sealed class ResponseTrace
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
    public string[] Tools { get; init; } = Array.Empty<string>();
    public string[] Skills { get; init; } = Array.Empty<string>();
    public string[] Subagents { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public ResponseUsageTrace? Usage { get; init; }
}

file sealed class ResponseUsageTrace
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
