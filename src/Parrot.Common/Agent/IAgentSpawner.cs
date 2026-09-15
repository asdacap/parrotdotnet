namespace Parrot.Agent;

/// <summary>Owns child-agent creation for one parent scope.</summary>
internal interface IAgentSpawner : IAsyncDisposable
{
    IAgentSessionScope SpawnScope(AgentLaunchRequest request);

    /// <summary>Returns the named retained scope or creates it using the supplied launch selection.</summary>
    IAgentSessionScope GetOrSpawnScope(string requestedName, Func<AgentLaunchRequest> selectLaunch);

    /// <summary>
    /// Creates a child with the requested name, unique among direct children, or resumes the existing
    /// idle child with that name by applying the request's model and profile; throws when the existing
    /// child is running. Scope and fork from the request are ignored when resuming.
    /// </summary>
    IAgentSessionScope SpawnOrResumeScope(AgentLaunchRequest request);
}
