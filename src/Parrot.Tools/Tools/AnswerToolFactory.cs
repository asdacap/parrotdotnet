using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerToolFactory(
    IChildQuestionCoordinator questions,
    IAgentParentScope parentScope,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("answer");

    public ITool Create(IAgentSession session) => new AnswerTool(questions, parentScope);
}
