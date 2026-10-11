using Parrot.Questions;

namespace Parrot.Tools;

/// <summary>Routes question requests to the user or the requesting agent's parent.</summary>
internal interface IQuestionRequester
{
    Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken);
}
