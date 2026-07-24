using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Tools.ApplyPatch;
using Parrot.Web;

namespace Parrot.Agent;

// Per user session, which is what lets it hold that session's tool factories:
// a factory is constructed with the owner it belongs to, and yields one tool
// instance per agent session from there.
internal sealed class AgentSessionFactory(
    UserSession owner,
    string workingDirectory,
    SystemContextBuilder systemContext,
    Compactor compactor,
    WebFetcher webFetcher) : IAgentSessionFactory
{
    private readonly ToolWorkspace _workspace = new(workingDirectory);

    private IReadOnlyList<IToolFactory> ToolFactories =>
        field ??=
        [
            new ExecCommandToolFactory(owner.ShellProcesses),
            new WaitShellToolFactory(owner.ShellProcesses),
            new ReadToolFactory(_workspace),
            new GlobToolFactory(_workspace),
            new GrepToolFactory(_workspace),
            new ApplyPatchToolFactory(workingDirectory),
            new GitDiffToolFactory(workingDirectory),
            new WebFetchToolFactory(webFetcher),
            new AgentSpawnToolFactory(owner.Registry),
            new WaitAgentToolFactory(owner.Registry),
        ];

    public AgentSession Create(
        string sessionId,
        ILLMProvider provider,
        string model,
        int depth,
        EventBroker eventBroker,
        EventRepository eventRepository,
        AgentIdentity? identity,
        CancellationToken lifetime) =>
        new(
            sessionId,
            provider,
            eventBroker,
            eventRepository,
            ToolFactories,
            systemContext,
            compactor,
            depth,
            identity,
            lifetime)
        {
            Model = model,
        };
}
