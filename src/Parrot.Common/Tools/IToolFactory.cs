using Parrot.Agent;

namespace Parrot.Tools;

// One factory per tool. A factory lives for one UserSession, so a tool needing
// its owner takes it by constructor rather than by parameter -- the factories
// that do not need it do not carry it. Create yields one instance per
// AgentSession.
internal interface IToolFactory
{
    // The model-facing definition of the tool Create yields.
    IToolDefinition Definition { get; }

    // Returning false excludes this tool from the supplied session's available tools.
    bool Supports(IAgentSession session) => true;

    ITool Create(IAgentSession session);
}
