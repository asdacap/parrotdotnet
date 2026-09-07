using Parrot.Config;
using Parrot.Llm;
using Parrot.Skills;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionFactory(
    IAgentSessionFactorySource agentSessionFactories,
    ModeRegistry modes,
    PromptTemplateCatalog promptTemplates,
    ProfileRegistry profiles,
    SkillCatalogFactory skillCatalogFactory,
    TimeSpan userInputTimeout,
    TimeProvider timeProvider) : IUserSessionFactory
{
    public Task<UserSession> Create(
        SessionResourceLease resources,
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
            new UserSessionModes(modes, promptTemplates),
            promptTemplates,
            profiles,
            skillCatalogFactory,
            interactivePermissions,
            userInputTimeout,
            timeProvider);
}
