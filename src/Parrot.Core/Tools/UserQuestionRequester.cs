using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class UserQuestionRequester(IQuestionBroker broker) : IQuestionRequester
{
    public Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken) =>
        broker.Ask(questions, cancellationToken);
}
