using Parrot.Process;

namespace Parrot.Tools;

internal sealed record ToolExecutionResult(string Text, YieldedShellProcess? YieldedProcess)
{
    public ToolExecutionResult(string text)
        : this(text, null)
    {
    }

    public static implicit operator ToolExecutionResult(string text) => new(text);
}
