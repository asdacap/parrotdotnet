using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerToolFactory(
    ChildQuestionCoordinator questions,
    IAgentSessionScope ownerScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AnswerTool(questions, ownerScope);
}
