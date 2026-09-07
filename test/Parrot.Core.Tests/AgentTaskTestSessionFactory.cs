using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskTestSessionFactory(ModelRouter router) : IAgentSessionFactory
{
    private static readonly ConcurrentBag<ShellProcessOwners> ProcessOwners = [];
    private static readonly ConcurrentBag<AgentQueueCatalog> QueueCatalogs = [];
    private readonly Lock _gate = new();
    private readonly List<AgentIdentity> _identities = [];
    private readonly List<string> _profileIds = [];
    private readonly List<IAgentSessionScope> _scopes = [];

    internal IReadOnlyList<AgentIdentity> Identities
    {
        get
        {
            lock (_gate)
            {
                return [.. _identities];
            }
        }
    }

    internal IReadOnlyList<string> ProfileIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _profileIds];
            }
        }
    }

    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        IAgentRegistry registry,
        CancellationToken lifetime)
    {
        lock (_gate)
        {
            _identities.Add(identity);
            _profileIds.Add(mode.Profile.Id);
        }

        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-agent-task-tests", Guid.NewGuid().ToString("N"))).FullName;
        var resources = new UserSessionResources(
            new StatePaths(root, root, root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(root));
        var owners = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), lifetime);
        ProcessOwners.Add(owners);
        var processes = owners.Prepare(identity.SessionId);
        owners.Register(processes);
        var queues = new AgentQueueCatalog(resources);
        QueueCatalogs.Add(queues);
        if (identity.ParentSessionId.Length > 0)
        {
            _ = queues.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName, TestModels.PromptTemplates));
        }

        var agentQueues = queues.Register(identity);
        var scope = TestAgentSessionScope.Build(identity, parentLink, registry, TestModels.PromptTemplates, (sessionParentScope, owningScope, children, childQuestions) =>
        {
            var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
            var completionCallbacks = new TestCompletionCallbacksFixture(
                childQuestions,
                new ActiveWorkCompletionReminder(children, processes, TestModels.PromptTemplates, null),
                exitReminder,
                eventRepository,
                eventBroker).Callbacks;
            IAgentSession session = new AgentSession(
            identity,
            sessionParentScope,
            model,
            router,
            eventBroker,
            eventRepository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, root, root),
            new ToolOutputBlobStore(root),
            TestModels.CompactionGroupBlobs(),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(),
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            exitReminder,
            mode,
            completionCallbacks,
            new SecurityProfileTestFixture(securityProfile).Security,
            status,
            agentQueues,
            new AgentSessionActivity(TimeProvider.System),
            lifetime);
            agentQueues.Attach(session);
            return session;
        });
        lock (_gate)
        {
            _scopes.Add(scope);
        }

        TestModels.RegisterScope(scope);
        return scope;
    }

    internal IAgentSessionScope ResolveScope(string name)
    {
        lock (_gate)
        {
            return _scopes.Single(scope => scope.Session.Name == name);
        }
    }
}
