using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionToolFactory(
    IQuestionBroker userQuestions,
    IAgentParentScope parentScope,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("question");

    public ITool Create(IAgentSession session) => new QuestionTool(session.Depth == 0
        ? new UserQuestionRequester(userQuestions)
        : new ChildQuestionRequester(parentScope.ChildQuestions, session));
}
