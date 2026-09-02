using Parrot.Questions;

namespace Parrot.Tools;

internal interface IQuestionRequester
{
    Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken);
}
