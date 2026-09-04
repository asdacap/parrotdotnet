using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionToolFactory(
    QuestionBroker userQuestions,
    AgentSessionParentScope parentScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new QuestionTool(session.Depth == 0
        ? new UserQuestionRequester(userQuestions)
        : new ChildQuestionRequester(parentScope.ChildQuestions, session));
}
