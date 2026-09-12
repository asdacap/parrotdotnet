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

internal sealed class AgentTaskTestSessionFactory(IModelRouter router) : IAgentSessionFactory
{
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

    public IEventRepository PrepareHistory(string agentSessionId, IEventRepository repository) => repository;

    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        IEventBroker eventBroker,
        IEventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        IRuntimeStatus status,
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
        var scope = TestAgentSessionScope.BuildWithResources(
            identity,
            parentLink,
            registry,
            TestModels.PromptTemplates,
            resources,
            new ProcessRunner(string.Empty),
            TestDiagnosticLog.Instance,
            (sessionParentScope, owningScope, children, childQuestions) =>
        {
            var processes = owningScope.Processes;
            var agentQueues = owningScope.GetService<IAgentQueues>();
            var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
            var completionCallbacks = new TestCompletionCallbacksFixture(
                childQuestions,
                new ActiveWorkCompletionReminder(children, processes, agentQueues, TestModels.PromptTemplates, null),
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
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
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
            TestDiagnosticLog.Instance,
            lifetime);
            return session;
        },
            lifetime);
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
