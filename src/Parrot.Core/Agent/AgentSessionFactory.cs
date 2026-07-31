using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

// Per user session, which is what lets it hold that session's tool factories:
// a factory is constructed with the owner it belongs to, and yields one tool
// instance per agent session from there.
internal sealed class AgentSessionFactory(
    UserSession owner,
    string workingDirectory,
    string blobDirectory,
    Compactor compactor,
    WebFetcher webFetcher,
    ModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    IAgentSessionScopeFactory scopes) : IAgentSessionFactory
{
    private readonly ToolWorkspace _workspace = new(workingDirectory);

    private IReadOnlyList<IToolFactory> ToolFactories =>
        field ??=
        [
            new ReadToolFactory(_workspace),
            new GlobToolFactory(_workspace),
            new GrepToolFactory(_workspace),
            new WriteToolFactory(_workspace),
            new EditToolFactory(_workspace),
            new WebFetchToolFactory(webFetcher),
            new AgentSpawnToolFactory(owner.Registry, router),
            new AgentSendToolFactory(owner.Registry),
            new WaitAgentToolFactory(owner.Registry),
            new WaitToolFactory(owner.Status, TimeProvider.System),
            new StatusToolFactory(owner.Status),
            new TodoReadToolFactory(),
            new TodoWriteToolFactory(),
            new QueueCreateToolFactory(),
            new QueueInfoToolFactory(),
            new QueueListenToolFactory(),
            new QueuePushToolFactory(),
            new QueueTakeToolFactory(),
            new QuestionToolFactory(owner.Questions),
            new RequestWritePermissionToolFactory(owner.Permissions),
        ];

    public IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IAgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        AgentRegistry registry,
        CancellationToken lifetime)
    {
        var queues = owner.QueueCatalog.Register(identity);
        try
        {
            return scopes.Create(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            ToolFactories,
            owner.ShellProcesses,
            systemPromptProvider,
            blobDirectory,
            compactor,
            profile,
            securityProfile,
            status,
            registry,
            queues,
            owner,
            lifetime);
        }
        catch
        {
            queues.Dispose();
            throw;
        }
    }
}
