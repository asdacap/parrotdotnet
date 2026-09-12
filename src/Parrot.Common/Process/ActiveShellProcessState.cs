using System.Diagnostics;

namespace Parrot.Process;

internal sealed record ActiveShellProcessState(
    string ProcessId,
    string Name,
    string Command,
    string OriginToolCallId,
    string OwnerAgentSessionId,
    string OwnerAgentName,
    string ParentAgentSessionId,
    string ParentAgentName,
    int Depth,
    long StartedTimestamp)
{
    public long ElapsedMilliseconds =>
        Math.Max(0, checked((long)Stopwatch.GetElapsedTime(StartedTimestamp).TotalMilliseconds));
}
