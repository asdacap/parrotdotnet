using Parrot.Agent;

namespace Parrot.Tools;

// One factory per tool. A factory lives for one UserSession, so a tool needing
// its owner takes it by constructor rather than by parameter -- the factories
// that do not need it do not carry it. Create yields one instance per
// AgentSession.
internal interface IToolFactory
{
    bool Supports(IAgentSession session) => true;

    ITool Create(IAgentSession session);
}
