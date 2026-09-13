namespace Parrot.Tools;

internal sealed class ReadOnlyExecCommandClassifier(IReadOnlyList<string> commandPrefixes)
{
    private readonly string[] _commandPrefixes = [.. commandPrefixes];

    public bool IsMatch(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var firstCommandCharacter = 0;
        while (firstCommandCharacter < command.Length && IsShellWhitespace(command[firstCommandCharacter]))
        {
            firstCommandCharacter++;
        }

        var commandStart = command[firstCommandCharacter..];
        return _commandPrefixes.Any(prefix =>
            commandStart.StartsWith(prefix, StringComparison.Ordinal)
            && (commandStart.Length == prefix.Length || IsShellWhitespace(commandStart[prefix.Length])));
    }

    private static bool IsShellWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';
}
