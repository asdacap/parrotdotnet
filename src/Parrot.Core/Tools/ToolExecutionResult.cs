using Parrot.Process;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed record ToolExecutionResult(
    string Text,
    YieldedShellProcess? YieldedProcess,
    IReadOnlyList<ImageArtifactMetadata> ImageArtifacts)
{
    public ToolExecutionResult(string text)
        : this(text, null, [])
    {
    }

    public ToolExecutionResult(string text, YieldedShellProcess? yieldedProcess)
        : this(text, yieldedProcess, [])
    {
    }

    public static implicit operator ToolExecutionResult(string text) => new(text);
}
