using System.Collections.Concurrent;
using GitHub.Copilot.SDK;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class AgentSessionManager
{
    private readonly ConcurrentDictionary<string, ManagedAgentSession> _activeSessions = new(StringComparer.Ordinal);

    public string? GetStableRouteName(string sessionId) =>
        _activeSessions.TryGetValue(sessionId, out var session) ? session.Profile.RouteName : null;

    public bool TryGetReusable(string sessionId, string desiredRouteName, out ManagedAgentSession session)
    {
        if (_activeSessions.TryGetValue(sessionId, out session!) &&
            string.Equals(session.Profile.RouteName, desiredRouteName, StringComparison.OrdinalIgnoreCase))
        {
            session.Touch();
            return true;
        }

        session = null!;
        return false;
    }

    public bool TryRemove(string sessionId, out ManagedAgentSession session) =>
        _activeSessions.TryRemove(sessionId, out session!);

    public void Track(string sessionId, CopilotSession sdkSession, AgentSessionProfile profile) =>
        _activeSessions[sessionId] = new ManagedAgentSession(sdkSession, profile);

    public IReadOnlyList<KeyValuePair<string, ManagedAgentSession>> RemoveExpired(TimeSpan idleTtl)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = new List<KeyValuePair<string, ManagedAgentSession>>();

        foreach (var pair in _activeSessions.ToArray())
        {
            if (now - pair.Value.LastUsedAt <= idleTtl)
            {
                continue;
            }

            if (_activeSessions.TryRemove(pair.Key, out var removed))
            {
                expired.Add(new KeyValuePair<string, ManagedAgentSession>(pair.Key, removed));
            }
        }

        return expired;
    }

    public IReadOnlyList<ManagedAgentSession> Snapshot() => _activeSessions.Values.ToArray();

    public void Clear() => _activeSessions.Clear();
}

public sealed class ManagedAgentSession
{
    public ManagedAgentSession(CopilotSession session, AgentSessionProfile profile)
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
