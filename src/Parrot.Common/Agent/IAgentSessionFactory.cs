using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Agent;

// The static half of an agent session -- provider, tools, sandbox, context,
// compactor -- belongs to the factory. Only what is genuinely per-session
// crosses this seam, which is what lets UserSession stop relaying five
// parameters it never uses.
internal interface IAgentSessionFactory
{
    /// <summary>Creates the destination agent's history view for transfer into its scope.</summary>
    IEventRepository PrepareHistory(AgentIdentity identity, IEventRepository repository);

    IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        IEventBroker eventBroker,
        IEventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        IAgentRegistry registry,
        CancellationToken lifetime);
}
