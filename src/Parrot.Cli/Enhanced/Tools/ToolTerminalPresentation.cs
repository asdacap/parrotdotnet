using System.Globalization;

namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolTerminalPresentation(
    ToolTerminalStatus Status,
    bool ResultPresent,
    string Result,
    string Error)
{
    public ToolTerminalStatus ResolveProcessStatus()
    {
        var status = ResolveStatus();
        if (status != Status || !ResultPresent)
        {
            return status;
        }

        const string exitPrefix = "Process exited with code ";
        if (!Result.StartsWith(exitPrefix, StringComparison.Ordinal))
        {
            return status;
        }

        var lineEnd = Result.IndexOf('\n', exitPrefix.Length);
        var exitCode = lineEnd < 0
            ? Result.AsSpan(exitPrefix.Length)
            : Result.AsSpan(exitPrefix.Length, lineEnd - exitPrefix.Length);
        return int.TryParse(exitCode, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            && value != 0
                ? ToolTerminalStatus.ReportedFailure
                : status;
    }

    public ToolTerminalStatus ResolveStatus() =>
        ResultPresent && Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : Status;
}
