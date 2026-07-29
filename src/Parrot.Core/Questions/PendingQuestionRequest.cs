namespace Parrot.Questions;

internal sealed record PendingQuestionRequest(string Id, IReadOnlyList<QuestionDefinition> Questions);
