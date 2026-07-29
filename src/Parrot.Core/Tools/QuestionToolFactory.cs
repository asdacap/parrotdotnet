using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionToolFactory(QuestionBroker broker) : IToolFactory
{
    public bool Supports(AgentSession session) => session.Depth == 0;

    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QuestionTool(broker);
}
