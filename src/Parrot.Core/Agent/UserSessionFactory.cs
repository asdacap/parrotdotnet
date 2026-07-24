using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories) : IUserSessionFactory
{
    public UserSession Create(
        string id,
        ILLMProvider provider,
        string providerId,
        string model,
        EventRepository eventRepository) =>
        new(
            id,
            provider,
            providerId,
            model,
            eventRepository,
            agentSessionFactories);
}
