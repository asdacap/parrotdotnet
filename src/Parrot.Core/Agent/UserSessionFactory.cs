using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Skills;
using Parrot.Statuses;
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
    Func<IAgentRegistry, IPromptTemplateCatalog, IReadOnlyList<IStatusProvider>> createRuntimeStatusProviders,
    Func<string, AgentTaskArtifact> parseTaskArtifact) : IUserSessionFactory
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
            new UserSessionModes(modes, promptTemplates, parseTaskArtifact),
            promptTemplates,
            profiles,
            skillCatalogFactory,
            interactivePermissions,
            userInputTimeout,
            timeProvider,
            static () => new EventBroker(),
            createRuntimeStatusProviders);
}
