using System.Collections;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

// The conversation, carried across turns so the agent remembers. It is
// restored from durable history and rebuilt from it after compaction.
internal sealed class AgentConversation(IAgentHistory history) : IReadOnlyList<LLMMessage>
{
    private readonly List<LLMMessage> _messages = Restore(history);

    public int Count => _messages.Count;

    public LLMMessage this[int index] => _messages[index];

    public void Add(LLMMessage message) => _messages.Add(message);

    // Rebuilds the conversation from durable history, as after compaction.
    public void Reload()
    {
        _messages.Clear();
        _messages.AddRange(Restore(history));
    }

    public LLMMessage Materialize(ConversationItem item) => Materialize(history, item);

    // Whether the model owes an answer. A history ending in a user prompt or a
    // tool result is unanswered; one ending in an assistant message is not.
    public bool IsAnswerable() =>
        _messages.LastOrDefault(message => message.Role != LLMRole.System)?.Role is LLMRole.User or LLMRole.Tool;

    public IEnumerator<LLMMessage> GetEnumerator() => _messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static List<LLMMessage> Restore(IAgentHistory source)
    {
        var context = source.CompactionHistory();
        if (context is null)
        {
            return [.. source.Conversation().Select(item => Materialize(source, item))];
        }

        var messages = new List<LLMMessage> { LLMMessage.System(context.Snapshot.Summary) };
        if (context.Status is not null)
        {
            messages.Add(Materialize(source, context.Status));
        }

        messages.AddRange(context.Tail.Select(item => Materialize(source, item)));
        return messages;
    }

    private static LLMMessage Materialize(IAgentHistory source, ConversationItem item) => new()
    {
        Role = item.Role,
        Contents = source.Materialize(item.Parts),
        ToolCalls = item.ToolCalls,
        ToolCallId = item.ToolCallId,
        ProviderContent = item.ProviderContent,
    };
}
