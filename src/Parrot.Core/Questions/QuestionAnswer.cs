namespace Parrot.Questions;

internal sealed record QuestionAnswer(string QuestionId, IReadOnlyList<string> OptionIds, string Custom);
