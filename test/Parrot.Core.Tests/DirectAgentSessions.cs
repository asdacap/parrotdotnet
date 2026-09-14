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
    private readonly List<IAgentQueues> _queues = [];
    private readonly List<IAgentSession> _sessions = [];
    private readonly List<IUserSession> _owners = [];
    private IModelRouter? _router;
    private TimeProvider _timeProvider = TimeProvider.System;
    private bool _includeStatusTool;

    public IReadOnlyList<AgentIdentity> Identities => _identities;

    public IReadOnlyList<AgentSessionSecurity> Securities => _securities;

    public IReadOnlyList<IAgentQueues> Queues => _queues;

    public IReadOnlyList<IAgentSession> Sessions => _sessions;

    public IReadOnlyList<IUserSession> Owners => _owners;

    public void IncludeStatusTool() => _includeStatusTool = true;

    public void Use(IModelRouter router) => _router = router;

    public void UseTimeProvider(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public IAgentSessionFactory Create(IUserSession owner)
    {
        _owners.Add(owner);
        return new OwnerAgentSessions(this, owner);
    }

    private sealed class OwnerAgentSessions(DirectAgentSessions source, IUserSession owner) : IAgentSessionFactory
    {
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
            IRuntimeStatus status,
            IAgentRegistry registry,
            CancellationToken lifetime)
        {
            eventRepository.RefreshAgentHistory(identity.SessionId);
            source._identities.Add(identity);
            var router = source._router ?? throw new InvalidOperationException("model router is not configured");
            var security = new SecurityProfileTestFixture(securityProfile).Security;
            source._securities.Add(security);
            var scope = TestAgentSessionScope.BuildWithResources(
                identity,
                parentLink,
                registry,
                TestModels.PromptTemplates,
                owner.Resources,
                new ProcessRunner(string.Empty),
                owner.Diagnostics,
                (sessionParentScope, owningScope, children, childQuestions) =>
            {
                var processes = owningScope.GetService<IProcessOwner>();
                var queues = owningScope.GetService<IAgentQueues>();
                source._queues.Add(queues);
                var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
                IAgentSession session = new AgentSession(identity, sessionParentScope, model, router, eventBroker, eventRepository, source._includeStatusTool ? [new StatusToolFactory(owner.Status)] : [], source._includeStatusTool ? new TestToolDefinitionsFixture("status").Definitions : TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(owner.Diagnostics, identity.SessionId, null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(children, identity), new ProcessActiveWorkBlocker(processes), new QueueActiveWorkBlocker(queues, TestModels.PromptTemplates)], TestModels.PromptTemplates), exitReminder, eventRepository, eventBroker).Callbacks, security, status, new AgentSessionActivity(source._timeProvider), owner.Diagnostics, lifetime);
                source._sessions.Add(session);
                return session;
            },
                lifetime);
            TestModels.RegisterScope(scope);
            return scope;
        }
    }
}
