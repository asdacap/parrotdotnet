using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

// Builds real agent sessions with no tools. The production factory is composed
// around a ProcessRunner and a sandbox, which a test of the wire contract has
// no use for -- but the session itself has to be real, because the drain is
// what the contract is a contract over.
internal sealed class DirectAgentSessions : IAgentSessionFactorySource
{
    private readonly List<AgentIdentity> _identities = [];
    private readonly List<AgentSessionSecurity> _securities = [];
    private readonly List<AgentQueues> _queues = [];
    private readonly List<IAgentSession> _sessions = [];
    private readonly List<UserSession> _owners = [];
    private ModelRouter? _router;
    private TimeProvider _timeProvider = TimeProvider.System;
    private bool _includeStatusTool;

    public IReadOnlyList<AgentIdentity> Identities => _identities;

    public IReadOnlyList<AgentSessionSecurity> Securities => _securities;

    public IReadOnlyList<AgentQueues> Queues => _queues;

    public IReadOnlyList<IAgentSession> Sessions => _sessions;

    public IReadOnlyList<UserSession> Owners => _owners;

    public void IncludeStatusTool() => _includeStatusTool = true;

    public void Use(ModelRouter router) => _router = router;

    public void UseTimeProvider(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public IAgentSessionFactory Create(UserSession owner)
    {
        _owners.Add(owner);
        return new OwnerAgentSessions(this, owner);
    }

    public ShellProcessOwners CreateShellProcesses(UserSession owner) =>
        new(owner.Resources, new ProcessRunner(string.Empty), owner.Diagnostics, owner.Lifetime);

    public AgentQueueCatalog CreateQueueCatalog(UserSession owner) =>
        new(owner.Resources, owner.Diagnostics);

    private sealed class OwnerAgentSessions(DirectAgentSessions source, UserSession owner) : IAgentSessionFactory
    {
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
            source._identities.Add(identity);
            var processes = owner.ShellProcesses.Prepare(identity.SessionId);
            owner.ShellProcesses.Register(processes);
            var router = source._router ?? throw new InvalidOperationException("model router is not configured");
            var queues = owner.QueueCatalog.Register(identity);
            source._queues.Add(queues);
            var security = new SecurityProfileTestFixture(securityProfile).Security;
            source._securities.Add(security);
            var scope = TestAgentSessionScope.Build(identity, parentLink, registry, TestModels.PromptTemplates, (sessionParentScope, owningScope, children, childQuestions) =>
            {
                var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
                IAgentSession session = new AgentSession(identity, sessionParentScope, model, router, eventBroker, eventRepository, source._includeStatusTool ? [new StatusToolFactory(owner.Status)] : [], source._includeStatusTool ? new TestToolDefinitionsFixture("status").Definitions : TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(owner.Diagnostics, identity.SessionId), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder(children, processes, TestModels.PromptTemplates, null), exitReminder, eventRepository, eventBroker).Callbacks, security, status, queues, new AgentSessionActivity(source._timeProvider), owner.Diagnostics, lifetime);
                queues.Attach(session);
                source._sessions.Add(session);
                return session;
            });
            TestModels.RegisterScope(scope);
            return scope;
        }
    }
}
