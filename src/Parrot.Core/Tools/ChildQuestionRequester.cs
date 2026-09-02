using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class ChildQuestionRequester(
    AgentRegistry agents,
    AgentSession session) : IQuestionRequester
{
    public Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken) =>
        agents.ResolveDirectParentQuestions(session).Ask(session, questions, cancellationToken);
}
