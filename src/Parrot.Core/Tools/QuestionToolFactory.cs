using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionToolFactory(
    QuestionBroker userQuestions,
    AgentRegistry agents) : IToolFactory
{
    public ITool Create(AgentSession session) => new QuestionTool(session.Depth == 0
        ? new UserQuestionRequester(userQuestions)
        : new ChildQuestionRequester(agents, session));
}
