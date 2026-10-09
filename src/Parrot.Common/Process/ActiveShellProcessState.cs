using System.Diagnostics;
using Parrot.Protocol;

namespace Parrot.Process;

internal sealed record ActiveShellProcessState(
    string ProcessId,
    string Name,
    string Command,
    string Description,
    string OriginToolCallId,
    string OwnerAgentSessionId,
    string OwnerAgentName,
    string ParentAgentSessionId,
    string ParentAgentName,
    int Depth,
    long StartedTimestamp,
    ShellProcessActivityKind ActivityKind)
{
    public long ElapsedMilliseconds =>
        Math.Max(0, checked((long)Stopwatch.GetElapsedTime(StartedTimestamp).TotalMilliseconds));
}
