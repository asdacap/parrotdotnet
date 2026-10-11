using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Skills;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories,
    ModeRegistry modes,
    IPromptTemplateCatalog promptTemplates,
    ProfileRegistry profiles,
    SkillCatalogFactory skillCatalogFactory,
    TimeSpan userInputTimeout,
    TimeProvider timeProvider,
    IReadOnlyDictionary<string, IModeWorkflowFactory> modeWorkflows) : IUserSessionFactory
{
    public Task<IUserSession> Create(
        ISessionResourceLease resources,
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode,
        bool interactivePermissions) =>
        UserSession.Create(
            id,
            rootAgentName,
            model,
            mode,
            resources,
            agentSessionFactories,
            new UserSessionModes(modes, modeWorkflows),
            promptTemplates,
            profiles,
            skillCatalogFactory,
            interactivePermissions,
            userInputTimeout,
            timeProvider,
            static () => new EventBroker());
}
