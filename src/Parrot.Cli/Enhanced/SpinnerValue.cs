namespace Parrot.Cli.Enhanced;

internal readonly record struct SpinnerValue(string Activity, int Frame)
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public string Render() => $"{Frames[Frame % Frames.Length]} {TerminalText.Sanitize(Activity)}";
}
