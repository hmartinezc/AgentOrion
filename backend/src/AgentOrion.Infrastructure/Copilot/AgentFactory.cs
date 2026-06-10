using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AgentOrion.Core.Configuration;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentSessionHandle
{
    public CopilotSession Session { get; }
    public AgentSessionProfile Profile { get; }
    public RoutingTrace RoutingTrace { get; }
    public bool IsTransient { get; }

    public AgentSessionHandle(CopilotSession session, AgentSessionProfile profile, RoutingTrace routingTrace, bool isTransient)
    {
        Session = session;
        Profile = profile;
        RoutingTrace = routingTrace;
        IsTransient = isTransient;
    }
}

public sealed class AgentFactory : IAsyncDisposable
{
    private static readonly TimeSpan SessionIdleTtl = TimeSpan.FromMinutes(20);
    private readonly AgentOrionOptions _options;
    private readonly AgentRequestRouter _router;
    private readonly SkillRegistry _skillRegistry;
    private readonly AgentSessionManager _sessions;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public AgentFactory(
        IOptions<AgentOrionOptions> options,
        AgentRequestRouter router,
        SkillRegistry skillRegistry,
        AgentSessionManager sessions)
    {
        _options = options.Value;
        _router = router;
        _skillRegistry = skillRegistry;
        _sessions = sessions;
    }

    public async Task<AgentSessionHandle> GetOrCreateSessionAsync(
        string sessionId,
        string prompt,
        string? mode,
        string? memoryContextAppendix = null,
        string? fallbackRouteName = null,
        IEnumerable<AIFunction>? tools = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await GetOrStartClientAsync();
        await CleanupExpiredSessionsAsync(client, ct);
        var normalizedMode = ChatModes.Normalize(mode);
        var toolCatalog = (tools ?? Enumerable.Empty<AIFunction>())
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var routingDecision = _router.SelectProfileWithTrace(
            prompt,
            toolCatalog,
            fallbackRouteName ?? _sessions.GetStableRouteName(sessionId));
        var desiredProfile = routingDecision.Profile;

        if (normalizedMode == ChatModes.Memory && _sessions.TryGetReusable(sessionId, desiredProfile.RouteName, out var reusableSession))
        {
            return new AgentSessionHandle(reusableSession.Session, reusableSession.Profile, routingDecision.Trace, isTransient: false);
        }

        if (normalizedMode == ChatModes.Memory && _sessions.TryRemove(sessionId, out var routeChangedSession))
        {
            await routeChangedSession.Session.DisposeAsync();
            try
            {
                await client.DeleteSessionAsync(sessionId, ct);
            }
            catch
            {
                // Best effort.
            }
        }

        return await CreateSpecialistSessionAsync(client, sessionId, normalizedMode, desiredProfile, routingDecision.Trace, memoryContextAppendix, ct);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(sessionId, out var activeSession))
        {
            await activeSession.Session.DisposeAsync();
        }

        if (_client is not null)
        {
            try
            {
                await _client.DeleteSessionAsync(sessionId, ct);
            }
            catch
            {
                // La sesión puede ya no existir; ignoramos
            }
        }
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await GetOrStartClientAsync();
    }

    private async Task<AgentSessionHandle> CreateSpecialistSessionAsync(
        CopilotClient client,
        string sessionId,
        string mode,
        AgentSessionProfile profile,
        RoutingTrace routingTrace,
        string? memoryContextAppendix,
        CancellationToken ct)
    {
        var skillDirs = _skillRegistry.ResolveDirectories(profile.SkillNames).ToList();

        var provider = BuildEffectiveProvider(profile.Provider, _options.Copilot.Provider);
        var model = ResolveModel(profile.Model, _options.Copilot.Model);

        var effectiveSessionId = mode == ChatModes.Fast
            ? $"{sessionId}-fast-{Guid.NewGuid():N}"[..Math.Min(sessionId.Length + 18, 64)]
            : sessionId;

        var session = await client.CreateSessionAsync(new SessionConfig
        {
            SessionId = effectiveSessionId,
            Model = model,
            Provider = provider,
            Streaming = true,
            SkillDirectories = skillDirs,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = $"{DomainGuard.SystemPromptAppendix}\n\nMODO ESPECIALISTA ACTIVO: {profile.DisplayName}. {profile.SpecialistPrompt}{memoryContextAppendix}"
            },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Hooks = new SessionHooks
            {
                OnUserPromptSubmitted = DomainGuard.OnUserPromptSubmittedAsync
            },
            Tools = profile.Tools.ToList()
        }, ct);

        if (mode == ChatModes.Memory)
        {
            _sessions.Track(sessionId, session, profile);
        }

        return new AgentSessionHandle(session, profile, routingTrace, isTransient: mode == ChatModes.Fast);
    }

    private async Task CleanupExpiredSessionsAsync(CopilotClient client, CancellationToken ct)
    {
        foreach (var pair in _sessions.RemoveExpired(SessionIdleTtl))
        {
            try { await pair.Value.Session.DisposeAsync(); } catch { }
            try { await client.DeleteSessionAsync(pair.Key, ct); } catch { }
        }
    }

    private async Task<CopilotClient> GetOrStartClientAsync()
    {
        if (_client is not null)
        {
            return _client;
        }

        await _clientLock.WaitAsync();
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            _client = new CopilotClient(new CopilotClientOptions
            {
                UseLoggedInUser = false,
                GitHubToken = null,
                LogLevel = "debug"
            });

            await _client.StartAsync();
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_client is not null)
        {
            foreach (var activeSession in _sessions.Snapshot())
            {
                try { await activeSession.Session.DisposeAsync(); } catch { }
            }

            try { await _client.StopAsync(); } catch { }
            _client.Dispose();
        }

        _clientLock.Dispose();
        _sessions.Clear();
    }

    private static ProviderConfig BuildEffectiveProvider(ProviderConfig? routeProvider, ProviderOptions globalProvider)
    {
        return new ProviderConfig
        {
            Type = Select(routeProvider?.Type, globalProvider.Type),
            BaseUrl = Select(routeProvider?.BaseUrl, globalProvider.BaseUrl),
            ApiKey = SelectNullable(routeProvider?.ApiKey, globalProvider.ApiKey),
            WireApi = Select(routeProvider?.WireApi, globalProvider.WireApi)
        };
    }

    private static string ResolveModel(string? routeModel, string globalModel) => Select(routeModel, globalModel);

    private static string Select(string? preferred, string fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    private static string? SelectNullable(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

}
