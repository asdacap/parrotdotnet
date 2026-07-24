namespace Parrot.Cli;

internal readonly record struct SpinnerValue(string Activity, int Frame)
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public string Render() => $"{Frames[Frame % Frames.Length]} {TerminalText.Sanitize(Activity)}";
}
