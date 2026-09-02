using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionToolFactory(
    QuestionBroker userQuestions,
    ChildQuestionCoordinator childQuestions) : IToolFactory
{
    public ITool Create(AgentSession session) => new QuestionTool(session.Depth == 0
        ? new UserQuestionRequester(userQuestions)
        : new ChildQuestionRequester(childQuestions, session));
}
