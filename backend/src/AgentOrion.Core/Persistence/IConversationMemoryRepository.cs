using AgentOrion.Core.Models;

namespace AgentOrion.Core.Persistence;

public interface IConversationMemoryRepository
{
    Task<ConversationMemoryState?> GetAsync(string sessionId);
    Task UpsertAsync(ConversationMemoryState state);
    Task DeleteAsync(string sessionId);
}
