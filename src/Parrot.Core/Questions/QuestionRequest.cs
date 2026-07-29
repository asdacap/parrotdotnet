namespace Parrot.Questions;

internal sealed record QuestionRequest(string Id, IReadOnlyList<QuestionDefinition> Questions);
