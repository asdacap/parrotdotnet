using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Tools.ApplyPatch;
using Parrot.Web;

namespace Parrot.Agent;

// Per user session, which is what lets it hold that session's tool factories:
// a factory is constructed with the owner it belongs to, and yields one tool
// instance per agent turn from there.
internal sealed class AgentSessionFactory(
    UserSession owner,
    string workingDirectory,
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
            new ExecCommandToolFactory(owner.ShellProcesses),
            new WaitProcessToolFactory(owner.ShellProcesses),
            new InterruptProcessToolFactory(owner.ShellProcesses),
            new ReadToolFactory(_workspace),
            new GlobToolFactory(_workspace),
            new GrepToolFactory(_workspace),
            new ApplyPatchToolFactory(workingDirectory),
            new WebFetchToolFactory(webFetcher),
            new AgentSpawnToolFactory(owner.Registry, router),
            new AgentSendToolFactory(owner.Registry),
            new WaitAgentToolFactory(owner.Registry),
            new StatusToolFactory(owner.Status),
            new TodoReadToolFactory(),
            new TodoWriteToolFactory(),
            new QuestionToolFactory(owner.Questions),
        ];

    public IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        AgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        CancellationToken lifetime) =>
        scopes.Create(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            ToolFactories,
            systemPromptProvider,
            compactor,
            profile,
            securityProfile,
            status,
            lifetime);
}
