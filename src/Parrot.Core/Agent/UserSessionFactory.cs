using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(IAgentSessionFactory agentSessions) : IUserSessionFactory
{
    public UserSession Create(string id, string model, EventRepository eventRepository) =>
        new(id, model, eventRepository, agentSessions);
}
