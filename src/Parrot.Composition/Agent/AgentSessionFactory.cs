using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Skills;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactory(
    IUserSession owner,
    Parrot.Process.ProcessRunner processRunner,
    string workingDirectory,
    Compactor compactor,
    WebFetcher webFetcher,
    ToolDefinitionCatalog toolDefinitions,
    AgentTaskConfig agentTasks,
    AgentSendConfig agentSend,
    AgentLimitsConfig agentLimits,
    RequestLimitsConfig requestLimits,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    IModelRouter router,
    IReadOnlyList<ISystemPromptProvider> systemPromptProviders,
    IPromptTemplateCatalog promptTemplates,
    Func<AgentSessionScopeArguments, IAgentSessionScope, AgentSessionComposition> composeSession) : IAgentSessionFactory
{
    private readonly IImageArtifactRepository _images = owner.Images;

    public IAgentHistory PrepareHistory(AgentIdentity identity, IEventRepository repository)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(repository);
        var occupants = AgentDirectoryLineage.Resolve(repository.AgentLineage())
            .OccupantsOf(identity.NamePath, identity.SessionId);
        return repository.BindAgentHistory(identity.SessionId, new AgentHistoryFile(owner.Resources.AgentScratch(identity.NamePath), occupants));
    }

    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        IEventBroker eventBroker,
        IAgentHistory history,
        IUserSessionStatistics statistics,
        IAgentProfile profile,
        SecurityProfile securityProfile,
        IAgentRegistry registry,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(parentLink);
        var scratch = owner.Resources.AgentScratch(identity.NamePath);
        history.Refresh();
        IAgentPathEnvironment pathEnvironment = new AgentPathEnvironment(owner.Resources, scratch);
        var workspace = new ToolWorkspace(workingDirectory, pathEnvironment);
        var security = new AgentSessionSecurity(
            securityProfile,
            owner.Resources.Workspace,
            owner.Resources.Root);
        var agentSkills = new AgentSkills(owner.SkillCatalog, promptTemplates);
        var prompts = new CompositeSystemPromptProvider(
            "runtime:agent-session-system-prompt",
            [
                .. systemPromptProviders,
                new AgentPathEnvironmentProvider(pathEnvironment, promptTemplates),
                new ScratchDirectoryProvider(scratch, promptTemplates),
                new AgentSkillPromptProvider(agentSkills),
            ]);
        var arguments = new AgentSessionScopeArguments(
            identity,
            parentLink,
            model,
            router,
            eventBroker,
            history,
            statistics,
            workspace,
            _images,
            webFetcher,
            agentTasks,
            agentSend,
            agentLimits,
            requestLimits,
            toolDefinitions,
            readOnlyExecCommandPrefixes,
            processRunner,
            owner.Resources,
            prompts,
            scratch,
            pathEnvironment,
            compactor,
            promptTemplates,
            profile,
            security,
            agentSkills,
            owner.Permissions,
            registry,
            owner.Questions,
            owner.TimeProvider,
            owner.Diagnostics,
            lifetime);
        var scope = new AgentSessionScope(arguments, composeSession);
        try
        {
            scope.GetService<IAgentQueues>().Initialize();
            return scope;
        }
        catch
        {
            scope.DisposeRejectedConstruction();
            throw;
        }
    }
}
