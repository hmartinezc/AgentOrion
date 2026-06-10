using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentOrion.Core.Configuration;
using AgentOrion.Core.Models;
using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Tools;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Options;

namespace AgentOrion.Infrastructure.Copilot;

public sealed record ChatTurnRequest(string Message, string? SessionId = null, string? Mode = null);

public sealed class ChatStreamEvent
{
    public string Type { get; init; } = string.Empty;
    public string? Content { get; init; }
    public string? Tool { get; init; }
    public bool? Success { get; init; }
    public string? Error { get; init; }
    public string? Agent { get; init; }
    public AgentResponseTrace? Trace { get; init; }
}

public sealed class ChatTurnService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AgentFactory _agentFactory;
    private readonly ConversationMemoryService _memoryService;
    private readonly ConversationTurnCoordinator _turnCoordinator;
    private readonly WorkflowCoordinator _workflowCoordinator;
    private readonly AgentOrionOptions _options;
    private readonly IConversationMemoryRepository _memoryRepository;
    private readonly IAgentAuditRepository _auditRepository;
    private readonly AgentCatalogProvider _catalogProvider;
    private readonly ToolCatalog _toolCatalog;
    private readonly AgentPermissionPolicy _permissionPolicy;

    public ChatTurnService(
        AgentFactory agentFactory,
        ConversationMemoryService memoryService,
        ConversationTurnCoordinator turnCoordinator,
        WorkflowCoordinator workflowCoordinator,
        IOptions<AgentOrionOptions> options,
        IConversationMemoryRepository memoryRepository,
        IAgentAuditRepository auditRepository,
        AgentCatalogProvider catalogProvider,
        ToolCatalog toolCatalog,
        AgentPermissionPolicy permissionPolicy)
    {
        _agentFactory = agentFactory;
        _memoryService = memoryService;
        _turnCoordinator = turnCoordinator;
        _workflowCoordinator = workflowCoordinator;
        _options = options.Value;
        _memoryRepository = memoryRepository;
        _auditRepository = auditRepository;
        _catalogProvider = catalogProvider;
        _toolCatalog = toolCatalog;
        _permissionPolicy = permissionPolicy;
    }

    public async IAsyncEnumerable<ChatStreamEvent> ProcessAsync(
        ChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N")[..12];
        var chatMode = ChatModes.Normalize(request.Mode);
        var trace = new AgentResponseTraceBuilder(sessionId, request.Message, _options.Copilot.Model);
        trace.RecordMode(chatMode);

        await using var turnLease = await _turnCoordinator.AcquireAsync(sessionId, ct);
        var memoryState = chatMode == ChatModes.Memory
            ? await _memoryRepository.GetAsync(sessionId) ?? _memoryService.Create(sessionId)
            : null;

        if (memoryState is not null)
        {
            _memoryService.ApplyPrompt(memoryState, request.Message);
        }

        var deterministicResult = await _workflowCoordinator.TryHandleAsync(
            new WorkflowContext(request.Message, memoryState, sessionId, chatMode),
            ct);

        if (deterministicResult is not null)
        {
            await foreach (var streamEvent in HandleDeterministicTurnAsync(request, deterministicResult, memoryState, trace, ct))
            {
                yield return streamEvent;
            }

            yield break;
        }

        await foreach (var streamEvent in HandleLlmTurnAsync(request, sessionId, chatMode, memoryState, trace, ct))
        {
            yield return streamEvent;
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> HandleDeterministicTurnAsync(
        ChatTurnRequest request,
        DeterministicTurnResult deterministicResult,
        ConversationMemoryState? memoryState,
        AgentResponseTraceBuilder trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var routingTrace = BuildDeterministicRoutingTrace(deterministicResult);
        trace.RecordRouting(routingTrace);

        var routeDef = _catalogProvider.Catalog.Routes
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
            await _memoryRepository.UpsertAsync(memoryState);
        }

        trace.Complete();
        var builtTrace = trace.Build();
        await _auditRepository.CreateAsync(BuildAuditLog(request, builtTrace, deterministicResult.Content));

        yield return new ChatStreamEvent { Type = "delta", Content = deterministicResult.Content };
        yield return new ChatStreamEvent { Type = "final", Content = deterministicResult.Content };
        yield return new ChatStreamEvent { Type = "trace", Trace = builtTrace };
        yield return new ChatStreamEvent { Type = "done" };
    }

    private async IAsyncEnumerable<ChatStreamEvent> HandleLlmTurnAsync(
        ChatTurnRequest request,
        string sessionId,
        string chatMode,
        ConversationMemoryState? memoryState,
        AgentResponseTraceBuilder trace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tools = _toolCatalog.CreateAllTools();
        var sessionHandle = await _agentFactory.GetOrCreateSessionAsync(
            sessionId,
            request.Message,
            chatMode,
            memoryContextAppendix: _memoryService.BuildContextAppendix(memoryState),
            fallbackRouteName: memoryState?.LastRouteName,
            tools: tools,
            ct: ct);
        var session = sessionHandle.Session;
        trace.RecordRouting(sessionHandle.RoutingTrace);
        foreach (var skillName in sessionHandle.Profile.SkillNames)
        {
            trace.RecordSkill(skillName);
        }

        if (memoryState is not null)
        {
            memoryState.LastRouteName = sessionHandle.Profile.RouteName;
        }

        var stream = Channel.CreateUnbounded<ChatStreamEvent>();
        string? finalContent = null;
        var subscription = session.On(evt =>
        {
            if (ct.IsCancellationRequested) return;

            ChatStreamEvent? payload = null;

            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    payload = new ChatStreamEvent { Type = "delta", Content = delta.Data.DeltaContent };
                    break;
                case AssistantMessageEvent msg:
                    finalContent = msg.Data.Content;
                    payload = new ChatStreamEvent { Type = "final", Content = msg.Data.Content };
                    break;
                case ToolExecutionStartEvent toolStart:
                    trace.RecordToolStart(toolStart.Data.ToolCallId, toolStart.Data.ToolName);
                    trace.RecordToolPermission(toolStart.Data.ToolCallId, _permissionPolicy.RequiresConfirmation(toolStart.Data.ToolName));
                    payload = new ChatStreamEvent { Type = "tool_start", Tool = toolStart.Data.ToolName };
                    break;
                case ToolExecutionCompleteEvent toolDone:
                {
                    var completedToolName = trace.RecordToolCompletion(toolDone.Data.ToolCallId);
                    trace.RecordToolResult(
                        toolDone.Data.ToolCallId,
                        completedToolName,
                        toolDone.Data.Success,
                        toolDone.Data.Error?.Message,
                        toolDone.Data.Result?.DetailedContent ?? toolDone.Data.Result?.Content);
                    if (memoryState is not null)
                    {
                        _memoryService.ApplyToolResult(
                            memoryState,
                            completedToolName ?? string.Empty,
                            toolDone.Data.Result?.DetailedContent ?? toolDone.Data.Result?.Content);
                    }

                    payload = new ChatStreamEvent
                    {
                        Type = "tool_done",
                        Tool = completedToolName,
                        Success = toolDone.Data.Success,
                        Error = toolDone.Data.Error?.Message
                    };
                    break;
                }
                case SkillInvokedEvent skillInvoked:
                    trace.RecordSkill(skillInvoked.Data.Name);
                    break;
                case SubagentSelectedEvent selected:
                    trace.RecordSubagent(selected.Data.AgentDisplayName ?? selected.Data.AgentName);
                    payload = new ChatStreamEvent { Type = "subagent_selected", Agent = selected.Data.AgentDisplayName ?? selected.Data.AgentName };
                    break;
                case SubagentStartedEvent started:
                    trace.RecordSubagent(started.Data.AgentDisplayName ?? started.Data.AgentName);
                    payload = new ChatStreamEvent { Type = "subagent_started", Agent = started.Data.AgentDisplayName ?? started.Data.AgentName };
                    break;
                case SubagentCompletedEvent completed:
                    trace.RecordSubagent(completed.Data.AgentDisplayName ?? completed.Data.AgentName);
                    payload = new ChatStreamEvent { Type = "subagent_completed", Agent = completed.Data.AgentDisplayName ?? completed.Data.AgentName };
                    break;
                case SubagentFailedEvent failed:
                    trace.RecordSubagent(failed.Data.AgentDisplayName ?? failed.Data.AgentName);
                    payload = new ChatStreamEvent { Type = "subagent_failed", Agent = failed.Data.AgentDisplayName ?? failed.Data.AgentName, Error = failed.Data.Error };
                    break;
                case AssistantUsageEvent usage:
                    trace.RecordUsage(usage.Data);
                    break;
                case SessionUsageInfoEvent usageInfo:
                    trace.RecordSessionUsage(usageInfo.Data);
                    break;
            }

            if (payload is not null)
            {
                stream.Writer.TryWrite(payload);
            }
        });

        var sendTask = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await session.SendAndWaitAsync(new MessageOptions { Prompt = request.Message });
                }
                catch (Exception ex)
                {
                    trace.RecordError(ex.Message);
                    stream.Writer.TryWrite(new ChatStreamEvent { Type = "error", Content = ex.Message });
                }
                finally
                {
                    if (sessionHandle.IsTransient)
                    {
                        await _agentFactory.DeleteSessionAsync(session.SessionId, ct);
                    }

                    if (memoryState is not null)
                    {
                        memoryState.UpdatedAt = DateTime.UtcNow;
                        await _memoryRepository.UpsertAsync(memoryState);
                    }

                    trace.Complete();
                    var builtTrace = trace.Build();
                    await _auditRepository.CreateAsync(BuildAuditLog(request, builtTrace, finalContent));
                    stream.Writer.TryWrite(new ChatStreamEvent { Type = "trace", Trace = builtTrace });
                    stream.Writer.TryWrite(new ChatStreamEvent { Type = "done" });
                }
            }
            finally
            {
                subscription.Dispose();
                stream.Writer.TryComplete();
            }
        }, ct);

        await foreach (var streamEvent in stream.Reader.ReadAllAsync(ct))
        {
            yield return streamEvent;
        }

        await sendTask;
    }

    private RoutingTrace BuildDeterministicRoutingTrace(DeterministicTurnResult deterministicResult)
    {
        return new RoutingTrace
        {
            SelectedRouteName = deterministicResult.RouteName,
            SelectedRouteDisplayName = deterministicResult.RouteDisplayName,
            Confidence = 1,
            Reason = "Turno resuelto por workflow deterministico antes de crear una sesion LLM.",
            Candidates =
            [
                new RoutingCandidateTrace
                {
                    RouteName = deterministicResult.RouteName,
                    DisplayName = deterministicResult.RouteDisplayName,
                    Score = 100,
                    MatchedSignals = ["deterministic_workflow"],
                    Selected = true
                }
            ]
        };
    }

    private static AgentAuditLog BuildAuditLog(ChatTurnRequest request, AgentResponseTrace trace, string? finalContent) => new()
    {
        SessionId = trace.SessionId,
        UserPrompt = request.Message,
        AgentResponse = finalContent,
        ToolCalled = trace.Tools.Length == 0 ? null : string.Join(",", trace.Tools),
        ToolOutputJson = trace.ToolExecutions.Length == 0 ? null : JsonSerializer.Serialize(trace.ToolExecutions, JsonOptions),
        RouteName = trace.RouteName,
        RouteDisplayName = trace.RouteDisplayName,
        Model = trace.Model,
        ChatMode = trace.ChatMode,
        ToolsJson = JsonSerializer.Serialize(trace.Tools, JsonOptions),
        SkillsJson = JsonSerializer.Serialize(trace.Skills, JsonOptions),
        Error = trace.Error,
        UsageJson = trace.Usage is null ? null : JsonSerializer.Serialize(trace.Usage, JsonOptions),
        RoutingTraceJson = trace.Routing is null ? null : JsonSerializer.Serialize(trace.Routing, JsonOptions),
        RouteConfidence = trace.Routing?.Confidence,
        RoutingReason = trace.Routing?.Reason,
        DurationMs = trace.DurationMs,
        WasOffTopic = string.Equals(trace.Error, "BLOCKED_BY_DOMAIN_GUARD", StringComparison.OrdinalIgnoreCase)
    };
}
