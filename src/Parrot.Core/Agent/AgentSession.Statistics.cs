namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    public AgentSessionStatisticsSnapshot CaptureStatistics() => _statistics.Capture();

    public void AddDescendantUsage(AgentUsageIncrement increment)
    {
        _statistics.AddDescendant(increment);
        parentScope.Parent?.Session.AddDescendantUsage(increment);
    }
}
