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
    RequestLimitsConfig requestLimits,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    IModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    IPromptTemplateCatalog promptTemplates,
    Func<AgentSessionScopeArguments, IAgentSessionScope, AgentSessionComposition> composeSession) : IAgentSessionFactory
{
    private readonly IImageArtifactRepository _images = owner.Images;

    public IEventRepository PrepareHistory(string agentSessionId, IEventRepository repository) =>
        repository.BindAgentHistory(new AgentHistoryFile(owner.Resources, agentSessionId));

    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        IEventBroker eventBroker,
        IEventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        IAgentRegistry registry,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(parentLink);
        var scratch = owner.Resources.AgentScratch(identity.SessionId);
        eventRepository.RefreshAgentHistory(identity.SessionId);
        var workspace = new ToolWorkspace(workingDirectory);
        IAgentPathEnvironment pathEnvironment = new AgentPathEnvironment(owner.Resources, scratch);
        var security = new AgentSessionSecurity(
            securityProfile,
            owner.Resources.Workspace,
            owner.Resources.ScratchRootDirectory);
        var agentSkills = new AgentSkills(owner.SkillCatalog, promptTemplates);
        var prompts = new CompositeSystemPromptProvider(
            "runtime:agent-session-system-prompt",
            [
                systemPromptProvider,
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
            eventRepository,
            workspace,
            _images,
            webFetcher,
            agentTasks,
            agentSend,
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
            mode,
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
