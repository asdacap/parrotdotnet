namespace Parrot.Questions;

internal sealed record QuestionDefinition(
    string Id,
    string Header,
    string Prompt,
    IReadOnlyList<QuestionOption> Options,
    bool Multiple,
    bool Custom);
