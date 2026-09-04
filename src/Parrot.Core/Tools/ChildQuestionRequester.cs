using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class ChildQuestionRequester(
    ChildQuestionCoordinator parentQuestions,
    IAgentSession session) : IQuestionRequester
{
    public Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken) =>
        parentQuestions.Ask(session, questions, cancellationToken);
}
