using System.Collections.Concurrent;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class ConversationTurnCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _turnLocks = new(StringComparer.Ordinal);

    public async ValueTask<ConversationTurnLease> AcquireAsync(string sessionId, CancellationToken ct = default)
    {
        var turnLock = _turnLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await turnLock.WaitAsync(ct);
        return new ConversationTurnLease(turnLock);
    }

    public readonly struct ConversationTurnLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _turnLock;

        public ConversationTurnLease(SemaphoreSlim turnLock)
        {
            _turnLock = turnLock;
        }

        public ValueTask DisposeAsync()
        {
            _turnLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
