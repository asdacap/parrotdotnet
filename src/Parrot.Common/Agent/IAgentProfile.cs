using Parrot.Security;

namespace Parrot.Agent;

/// <summary>Supplies an agent's prompt, tool availability, turn limits, and security policy.</summary>
internal interface IAgentProfile
{
    /// <summary>Gets the configured profile identifier.</summary>
    string Id { get; }

    /// <summary>Gets the profile's description for choosing a child agent.</summary>
    string Usage { get; }

    /// <summary>Gets the maximum number of this profile in the agent's policy lineage.</summary>
    int RecursionLimit { get; }

    /// <summary>Gets the current prompt; mode-specific content may change between reads.</summary>
    string Prompt { get; }

    /// <summary>Gets the tool allowlist, or null when tools are not restricted by an allowlist.</summary>
    IReadOnlyList<string>? AllowedTools { get; }

    /// <summary>Gets the tools excluded from the agent's available tools.</summary>
    IReadOnlyList<string> DisabledTools { get; }

    /// <summary>Gets the maximum number of tool-call cycles in a turn.</summary>
    int MaxTurns { get; }

    /// <summary>Gets a value indicating whether outstanding active work must settle before turn completion.</summary>
    bool EnforceActiveWorkCompletion { get; }

    /// <summary>Gets a value indicating whether users may select this profile as a session mode.</summary>
    bool IsUserSelectable { get; }

    /// <summary>Gets a value indicating whether agents may select this profile when spawning children.</summary>
    bool IsAgentSelectable { get; }

    /// <summary>Gets the security policy supplied by the profile or its mode projection.</summary>
    SecurityProfile SecurityProfile { get; }

    /// <summary>True when the tool survives both the allowlist and the disabled list.</summary>
    bool IsToolPermitted(string toolName);
}
