namespace Parrot.Cli.Commands;

internal sealed class SlashDialogOption(string id, string label, string description)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string Description { get; } = description;

    public bool Match(string answer) => string.Equals(Id, answer, StringComparison.Ordinal);
}
