using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(IAgentSessionFactorySource agentSessionFactories) : IUserSessionFactory
{
    public UserSession Create(string id, string model, EventRepository eventRepository) =>
        new(id, model, eventRepository, agentSessionFactories);
}
