using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

// The static half of an agent session -- provider, tools, sandbox, context,
// compactor -- belongs to the factory. Only what is genuinely per-session
// crosses this seam, which is what lets UserSession stop relaying five
// parameters it never uses.
internal interface IAgentSessionFactory
{
    AgentSession Create(
        string sessionId,
        ILLMProvider provider,
        string model,
        int depth,
        EventBroker eventBroker,
        EventRepository eventRepository);
}
