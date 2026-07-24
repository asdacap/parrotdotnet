using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories,
    ModeRegistry modes) : IUserSessionFactory
{
    public UserSession Create(
        string id,
        ILLMProvider provider,
        string providerId,
        string model,
        string mode,
        EventRepository eventRepository) =>
        new(
            id,
            provider,
            providerId,
            model,
            mode,
            eventRepository,
            agentSessionFactories,
            modes);
}
