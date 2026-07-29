using Parrot.Cli.Commands;

namespace Parrot.Cli.Enhanced;

internal sealed class SlashCommandCompletion(SlashCommandRegistry registry)
{
    private List<ISlashCommand> _commands = [];

    public IReadOnlyList<ISlashCommand> Commands => _commands;

    public int Selected { get; private set; }

    public void Refresh(string entered)
    {
        ArgumentNullException.ThrowIfNull(entered);

        var selected = _commands.Count == 0 ? null : _commands[Selected];
        _commands = HasArguments(entered) ? [] : [.. registry.Complete(entered)];
        Selected = selected is null ? 0 : _commands.IndexOf(selected);
        if (Selected < 0)
        {
            Selected = 0;
        }
    }

    public void SelectPrevious()
    {
        if (_commands.Count > 0)
        {
            Selected = (Selected - 1 + _commands.Count) % _commands.Count;
        }
    }

    public void SelectNext()
    {
        if (_commands.Count > 0)
        {
            Selected = (Selected + 1) % _commands.Count;
        }
    }

    public string? Accept(string entered)
    {
        ArgumentNullException.ThrowIfNull(entered);

        if (_commands.Count == 0)
        {
            return null;
        }

        var end = entered.IndexOfAny([' ', '\t', '\r', '\n']);
        return _commands[Selected].Name + (end < 0 ? string.Empty : entered[end..]);
    }

    private static bool HasArguments(string entered) => entered.IndexOfAny([' ', '\t', '\r', '\n']) >= 0;
}
