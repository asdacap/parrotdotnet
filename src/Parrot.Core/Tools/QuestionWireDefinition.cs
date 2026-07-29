namespace Parrot.Tools;

internal sealed class QuestionWireDefinition
{
    public string? Id { get; init; }

    public string? Header { get; init; }

    public string? Prompt { get; init; }

    public QuestionWireOption[]? Options { get; init; }

    public bool Multiple { get; init; }

    public bool Custom { get; init; }
}
