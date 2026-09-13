using Parrot.Process;

namespace Parrot.Agent;

/// <summary>Provides the session's shell path defaults and applies per-command overrides.</summary>
internal interface IAgentPathEnvironment
{
    /// <summary>Returns the existing path environment defaults.</summary>
    IReadOnlyList<KeyValuePair<string, string>> Materialize();

    /// <summary>Combines path defaults with command overrides, which take precedence.</summary>
    ProcessEnvironmentOverrides Merge(ProcessEnvironmentOverrides overrides);
}
