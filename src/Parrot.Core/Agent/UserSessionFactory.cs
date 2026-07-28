using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories,
    ModeRegistry modes) : IUserSessionFactory
{
    public UserSession Create(
        string id,
        string rootAgentName,
        ProviderModel model,
        string mode,
        EventRepository eventRepository) =>
        new(
            id,
            rootAgentName,
            model,
            mode,
            eventRepository,
            agentSessionFactories,
            modes);
}
