using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactory(
    UserSession owner,
    string workingDirectory,
    Compactor compactor,
    WebFetcher webFetcher,
    ToolDefinitionCatalog toolDefinitions,
    AgentTaskConfig agentTasks,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    ModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    PromptTemplateCatalog promptTemplates,
    IAgentSessionScopeFactory scopes) : IAgentSessionFactory
{
    private readonly ImageArtifactRepository _images = owner.Images;

    public IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        AgentRegistry registry,
        CancellationToken lifetime)
    {
        var queues = owner.QueueCatalog.Register(identity);
        try
        {
            var scratch = owner.Resources.AgentScratch(identity.SessionId);
            _ = eventRepository.PrepareAgentHistory(identity.SessionId);
            var workspace = new ToolWorkspace(workingDirectory);
            var security = new AgentSessionSecurity(
                securityProfile,
                owner.Resources.Workspace,
                owner.Resources.ScratchRootDirectory);
            var prompts = new CompositeSystemPromptProvider(
                "runtime:agent-session-system-prompt",
                [systemPromptProvider, new ScratchDirectoryProvider(scratch, promptTemplates)]);
            return scopes.Create(
                identity,
                model,
                router,
                eventBroker,
                eventRepository,
                ToolFactories(workspace, eventBroker, eventRepository),
                toolDefinitions,
                readOnlyExecCommandPrefixes,
                owner.ShellProcesses,
                prompts,
                scratch,
                compactor,
                promptTemplates,
                mode,
                security,
                status,
                registry,
                queues,
                owner,
                owner.TimeProvider,
                lifetime);
        }
        catch
        {
            queues.Dispose();
            throw;
        }
    }

    private IReadOnlyList<IToolFactory> ToolFactories(
        ToolWorkspace workspace,
        EventBroker eventBroker,
        EventRepository eventRepository) =>
    [
        new ReadToolFactory(workspace),
        new ReadImageToolFactory(workspace, _images),
        new GlobToolFactory(workspace),
        new WriteToolFactory(workspace),
        new EditToolFactory(workspace),
        new WebFetchToolFactory(webFetcher),
        new AgentSpawnToolFactory(owner.Registry, router),
        new RunAgentTasksToolFactory(workspace, owner.Registry, router, eventBroker, eventRepository, agentTasks),
        new SetCheckpointToolFactory(),
        new AgentSendToolFactory(owner.Registry),
        new AgentStatusToolFactory(owner.Registry, owner.ShellProcesses),
        new WaitToolFactory(owner.Status, owner.TimeProvider),
        new StatusToolFactory(owner.Status),
        new QueueCreateToolFactory(),
        new QueueInfoToolFactory(),
        new QueueListenToolFactory(),
        new QueuePushToolFactory(workspace),
        new QueueTakeToolFactory(),
        new QuestionToolFactory(owner.Questions),
        new RequestWritePermissionToolFactory(owner.Permissions),
    ];
}
