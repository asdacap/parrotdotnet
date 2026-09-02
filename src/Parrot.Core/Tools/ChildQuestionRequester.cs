using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class ChildQuestionRequester(
    ChildQuestionCoordinator coordinator,
    AgentSession session) : IQuestionRequester
{
    public Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken) =>
        coordinator.Ask(session, questions, cancellationToken);
}
