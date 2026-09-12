namespace Parrot.Questions;

internal sealed record QuestionDefinition(
    string Header,
    string Prompt,
    IReadOnlyList<string> Options,
    bool Multiple,
    bool Custom);
