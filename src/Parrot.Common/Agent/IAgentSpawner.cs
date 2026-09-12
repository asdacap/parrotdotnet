namespace Parrot.Agent;

/// <summary>Owns child-agent creation and retained-agent reservations for one parent scope.</summary>
internal interface IAgentSpawner : IAsyncDisposable
{
    IAgentSessionScope SpawnScope(AgentLaunchRequest request);

    /// <summary>Returns the named retained scope or creates it using the supplied launch selection.</summary>
    IAgentSessionScope GetOrSpawnScope(string requestedName, Func<AgentLaunchRequest> selectLaunch);

    void ReleaseRetainedAgent(string sessionId);
}
