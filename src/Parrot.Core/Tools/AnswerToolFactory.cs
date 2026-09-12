using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerToolFactory(
    IChildQuestionCoordinator questions,
    IAgentParentScope parentScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AnswerTool(questions, parentScope);
}
