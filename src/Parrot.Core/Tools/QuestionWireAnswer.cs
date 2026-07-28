namespace Parrot.Tools;

internal sealed class QuestionWireAnswer
{
    public required string QuestionId { get; init; }

    public required string[] OptionIds { get; init; }

    public required string Custom { get; init; }
}
