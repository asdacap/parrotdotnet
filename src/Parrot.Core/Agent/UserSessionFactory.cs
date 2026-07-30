using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories,
    ModeRegistry modes) : IUserSessionFactory
{
    public UserSession Create(
        SessionResourceLease resources,
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode) =>
        new(
            id,
            rootAgentName,
            model,
            mode,
            resources,
            agentSessionFactories,
            new UserSessionModes(modes, resources.Resources.PlanDirectory));
}
