using Parrot.Agent;

namespace Parrot.Context;

/// <summary>Builds an agent-scoped system prompt contribution from epoch context and the current turn selection.</summary>
internal interface ISystemPrompt
{
    /// <summary>Refreshes context sampled once per epoch; static contributions may do nothing.</summary>
    void RenewEpoch();

    /// <summary>Renders for the selection using the current epoch; epoch-dependent contributions require renewal first.</summary>
    string Build(AgentTurnSelection selection);
}
