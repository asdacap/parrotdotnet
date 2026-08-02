using Parrot.Agent;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class ScratchDirectoryProvider(AgentScratchDirectory scratch) : ISystemPromptProvider
{
    public string Key => "runtime:user-session-context:01-agent-scratch";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(
            $"Persistent writable scratch directory for this agent: {scratch.Root}. "
            + "Use this directory for temporary files and durable artifacts that should remain available when this user session resumes.");
    }
}
