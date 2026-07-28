using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionToolFactory(QuestionBroker broker) : IToolFactory
{
    public ITool? Create(AgentSession session, AgentTurnSelection selection) =>
        session.Depth == 0 ? new QuestionTool(broker) : null;
}
