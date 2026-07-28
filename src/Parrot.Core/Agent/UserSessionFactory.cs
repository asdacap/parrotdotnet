using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories,
    ModeRegistry modes) : IUserSessionFactory
{
    public UserSession Create(
        string id,
        ProviderModel model,
        string mode,
        EventRepository eventRepository) =>
        new(
            id,
            model,
            mode,
            eventRepository,
            agentSessionFactories,
            modes);
}
