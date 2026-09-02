using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerToolFactory(ChildQuestionCoordinator questions) : IToolFactory
{
    public ITool Create(AgentSession session) => new AnswerTool(questions);
}
