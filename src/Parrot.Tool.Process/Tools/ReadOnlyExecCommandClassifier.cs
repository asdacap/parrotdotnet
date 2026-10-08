namespace Parrot.Tools;

internal sealed class ReadOnlyExecCommandClassifier(IReadOnlyList<string> commandPrefixes)
{
    public bool IsMatch(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandStart = command.TrimStart(' ', '\t', '\r', '\n');
        return commandPrefixes.Any(prefix =>
            commandStart.StartsWith(prefix, StringComparison.Ordinal)
            && (commandStart.Length == prefix.Length || IsShellWhitespace(commandStart[prefix.Length])));
    }

    private static bool IsShellWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';
}
