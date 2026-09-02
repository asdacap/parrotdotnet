using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class UserQuestionRequester(QuestionBroker broker) : IQuestionRequester
{
    public Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken) =>
        broker.Ask(questions, cancellationToken);
}
