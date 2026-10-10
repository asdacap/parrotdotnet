namespace Parrot.Protocol;

public sealed partial class YieldedShellProcess
{
    internal static YieldedShellProcess FromProcess(Process.YieldedShellProcess yielded)
    {
        var converted = new YieldedShellProcess
        {
            ProcessId = yielded.ProcessId,
            Name = yielded.Name,
            InventoryInstanceId = yielded.InventoryInstanceId,
            VisibleRevision = yielded.VisibleRevision,
            ActivityKind = yielded.ActivityKind,
        };
        if (yielded.StdoutPath is { } stdoutPath)
        {
            converted.StdoutPath = stdoutPath;
        }

        if (yielded.StderrPath is { } stderrPath)
        {
            converted.StderrPath = stderrPath;
        }

        return converted;
    }
}
