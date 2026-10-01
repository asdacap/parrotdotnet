using Parrot.Agent;

namespace Parrot.Context;

internal sealed class StandingInstructionProvider(StandingInstruction instruction) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:94-standing-instruction";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return instruction;
    }
}
