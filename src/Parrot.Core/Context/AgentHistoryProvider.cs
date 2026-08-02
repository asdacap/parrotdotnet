using Parrot.Agent;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class AgentHistoryProvider(UserSessionResources resources) : ISystemPromptProvider
{
    public string Key => "runtime:user-session-context:02-agent-history";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var path = resources.AgentHistoryFile(identity.SessionId);
        return new StaticSystemPrompt(
            $"Your durable message and compaction history is available as a read-only JSONL file: {path}. "
            + "Use the read tool when you need to inspect it.");
    }
}
