using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerToolFactory(
    ChildQuestionCoordinator questions,
    AgentSessionParentScope parentScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AnswerTool(questions, parentScope);
}
