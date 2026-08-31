using System.Globalization;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolTerminalPresentation(
    ToolTerminalStatus Status,
    bool ResultPresent,
    string Result,
    string Error,
    YieldedShellProcess? YieldedProcess)
{
    public ToolTerminalPresentation(
        ToolTerminalStatus status,
        bool resultPresent,
        string result,
        string error)
        : this(status, resultPresent, result, error, null)
    {
    }

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

        var codeEnd = Result.IndexOfAny([' ', '\n'], exitPrefix.Length);
        var exitCode = codeEnd < 0
            ? Result.AsSpan(exitPrefix.Length)
            : Result.AsSpan(exitPrefix.Length, codeEnd - exitPrefix.Length);
        var validSuffix = codeEnd < 0
            || Result[codeEnd] == '\n'
            || Result.AsSpan(codeEnd).StartsWith(" after ", StringComparison.Ordinal);
        return validSuffix
            && int.TryParse(exitCode, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            && value != 0
                ? ToolTerminalStatus.ReportedFailure
                : status;
    }

    public ToolTerminalStatus ResolveStatus() =>
        ResultPresent && Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : Status;

    public ToolBlock DescribeBlock(ToolBlockKind successKind)
    {
        var status = ResolveStatus();
        if (status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure)
        {
            return ToolBlock.FromError(ResultPresent ? Result : Error);
        }

        if (!ResultPresent || Result.Length == 0)
        {
            return ToolBlock.Empty;
        }

        return successKind switch
        {
            ToolBlockKind.Diff => ToolBlock.FromDiff(Result),
            ToolBlockKind.Todos => ToolBlock.FromTodos(Result),
            ToolBlockKind.Text => ToolBlock.FromText(Result),
            _ => ToolBlock.Empty,
        };
    }
}
