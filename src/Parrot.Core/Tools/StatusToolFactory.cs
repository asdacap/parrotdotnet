using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class StatusToolFactory(RuntimeStatus status) : IToolFactory
{
    public ITool? Create(AgentSession session, AgentTurnSelection selection) =>
        new StatusTool(status, session, selection);
}
