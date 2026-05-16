using System.Collections.Concurrent;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AgentOrion.Core.Configuration;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentSessionHandle
{
    public CopilotSession Session { get; }
    public AgentSessionProfile Profile { get; }
    public bool IsTransient { get; }

    public AgentSessionHandle(CopilotSession session, AgentSessionProfile profile, bool isTransient)
    {
        Session = session;
        Profile = profile;
        IsTransient = isTransient;
    }
}

public sealed class AgentFactory : IAsyncDisposable
{
    private static readonly TimeSpan SessionIdleTtl = TimeSpan.FromMinutes(20);
    private readonly AgentOrionOptions _options;
    private readonly AgentRequestRouter _router;
    private readonly string _contentRootPath;
    private readonly IReadOnlyDictionary<string, string> _skillPathByName;
    private readonly ConcurrentDictionary<string, SessionEntry> _activeSessions = new();
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public AgentFactory(IOptions<AgentOrionOptions> options, AgentRequestRouter router, string contentRootPath)
    {
        _options = options.Value;
        _router = router;
        _contentRootPath = contentRootPath;
        _skillPathByName = BuildSkillPathIndex();
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

        var desiredProfile = _router.SelectProfile(
            prompt,
            toolCatalog,
            fallbackRouteName ?? (_activeSessions.TryGetValue(sessionId, out var existingSession) ? existingSession.Profile.RouteName : null));

        if (normalizedMode == ChatModes.Memory && _activeSessions.TryGetValue(sessionId, out var activeSession))
        {
            if (string.Equals(activeSession.Profile.RouteName, desiredProfile.RouteName, StringComparison.OrdinalIgnoreCase))
            {
                activeSession.Touch();
                return new AgentSessionHandle(activeSession.Session, activeSession.Profile, isTransient: false);
            }

            _activeSessions.TryRemove(sessionId, out _);
            await activeSession.Session.DisposeAsync();

            try
            {
                await client.DeleteSessionAsync(sessionId, ct);
            }
            catch
            {
                // Best effort.
            }
        }

        return await CreateSpecialistSessionAsync(client, sessionId, normalizedMode, desiredProfile, memoryContextAppendix, ct);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_activeSessions.TryRemove(sessionId, out var activeSession))
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
        string? memoryContextAppendix,
        CancellationToken ct)
    {
        var skillDirs = profile.SkillNames
            .Select(skillName => _skillPathByName.TryGetValue(skillName, out var path) ? path : null)
            .OfType<string>()
            .ToList();

        var provider = new ProviderConfig
        {
            Type = _options.Copilot.Provider.Type,
            BaseUrl = _options.Copilot.Provider.BaseUrl,
            ApiKey = _options.Copilot.Provider.ApiKey,
            WireApi = _options.Copilot.Provider.WireApi
        };

        var effectiveSessionId = mode == ChatModes.Fast
            ? $"{sessionId}-fast-{Guid.NewGuid():N}"[..Math.Min(sessionId.Length + 18, 64)]
            : sessionId;

        var session = await client.CreateSessionAsync(new SessionConfig
        {
            SessionId = effectiveSessionId,
            Model = _options.Copilot.Model,
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
            _activeSessions[sessionId] = new SessionEntry(session, profile);
        }

        return new AgentSessionHandle(session, profile, isTransient: mode == ChatModes.Fast);
    }

    private async Task CleanupExpiredSessionsAsync(CopilotClient client, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in _activeSessions.ToArray())
        {
            if (now - pair.Value.LastUsedAt <= SessionIdleTtl)
            {
                continue;
            }

            if (!_activeSessions.TryRemove(pair.Key, out var expired))
            {
                continue;
            }

            try { await expired.Session.DisposeAsync(); } catch { }
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
            foreach (var activeSession in _activeSessions.Values)
            {
                try { await activeSession.Session.DisposeAsync(); } catch { }
            }

            try { await _client.StopAsync(); } catch { }
            _client.Dispose();
        }

        _clientLock.Dispose();
        _activeSessions.Clear();
    }

    private IReadOnlyDictionary<string, string> BuildSkillPathIndex()
    {
        return _options.SkillDirectories
            .Select(path => Path.GetFullPath(path, _contentRootPath))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetDirectories(root))
            .ToDictionary(
                path => Path.GetFileName(path),
                path => path,
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SessionEntry
    {
        public SessionEntry(CopilotSession session, AgentSessionProfile profile)
        {
            Session = session;
            Profile = profile;
            LastUsedAt = DateTimeOffset.UtcNow;
        }

        public CopilotSession Session { get; }
        public AgentSessionProfile Profile { get; }
        public DateTimeOffset LastUsedAt { get; private set; }

        public void Touch() => LastUsedAt = DateTimeOffset.UtcNow;
    }
}
